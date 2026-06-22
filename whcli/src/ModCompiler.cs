using System.Diagnostics;
using System.Text;

namespace Whcli;

/// <summary>
/// Compiles a mod's .wh.cpp into per-architecture DLLs using Windhawk's bundled
/// clang++. Faithful port of vscode-windhawk/src/utils/compilerUtils.ts (the local
/// compilation path), minus the precompiled-headers optimization which the install
/// flow does not use.
/// </summary>
internal sealed class ModCompiler
{
    private readonly string _compilerPath;
    private readonly string _enginePath;
    private readonly string _engineModsPath;
    private readonly bool _arm64;
    private readonly string[] _supportedTargets;

    private static readonly string[] CommonSystemModTargets =
    [
        "startmenuexperiencehost.exe", "searchhost.exe", "explorer.exe",
        "shellexperiencehost.exe", "shellhost.exe", "dwm.exe", "notepad.exe", "regedit.exe"
    ];

    public ModCompiler(WindhawkInstall install, bool arm64Enabled)
    {
        _compilerPath = install.CompilerPath;
        _enginePath = install.EnginePath;
        _engineModsPath = install.EngineModsPath;
        _arm64 = arm64Enabled;
        _supportedTargets = arm64Enabled
            ? ["i686-w64-mingw32", "x86_64-w64-mingw32", "aarch64-w64-mingw32"]
            : ["i686-w64-mingw32", "x86_64-w64-mingw32"];

        foreach (var t in _supportedTargets)
            CopyCompilerLibs(t);
    }

    private static string Sub(string target) => target switch
    {
        "i686-w64-mingw32" => "32",
        "x86_64-w64-mingw32" => "64",
        "aarch64-w64-mingw32" => "arm64",
        _ => throw new InvalidOperationException("bad target")
    };

    private List<string> TargetsFor(string[] architectures, string[] modTargets)
    {
        if (architectures.Length == 0) architectures = ["x86", "x86-64"];
        var targets = new List<string>();
        foreach (var a in architectures)
        {
            switch (a)
            {
                case "x86": targets.Add("i686-w64-mingw32"); break;
                case "x86-64":
                    if (_arm64)
                    {
                        targets.Add("aarch64-w64-mingw32");
                        bool allCommon = modTargets.Length > 0 &&
                            modTargets.All(t => CommonSystemModTargets.Contains(t.ToLowerInvariant()));
                        if (!allCommon) targets.Add("x86_64-w64-mingw32");
                    }
                    else targets.Add("x86_64-w64-mingw32");
                    break;
                case "amd64": targets.Add("x86_64-w64-mingw32"); break;
                case "arm64": if (_arm64) targets.Add("aarch64-w64-mingw32"); break;
                default: throw new InvalidOperationException($"Unsupported architecture: {a}");
            }
        }
        if (targets.Count == 0) throw new InvalidOperationException("The current architecture is not supported");
        return targets;
    }

    private bool CompiledModExists(string fileName, string target)
        => File.Exists(Path.Combine(_engineModsPath, Sub(target), fileName));

    /// <summary>Compile all targets for a mod; returns the generated library file name.</summary>
    public string CompileMod(string modId, string version, string[] include, string source,
                             string[] architectures, string? compilerOptions)
    {
        var rng = Random.Shared;
        string dllName;
        do { dllName = $"{modId}_{version}_{rng.Next(100000, 1000000)}.dll"; }
        while (_supportedTargets.Any(t => CompiledModExists(dllName, t)));

        string[] extraArgs = string.IsNullOrWhiteSpace(compilerOptions) ? [] : SplitArgs(compilerOptions);

        foreach (var target in TargetsFor(architectures, include))
            CompileOne(source, dllName, target, modId, version, extraArgs);

        return dllName;
    }

    /// <summary>Remove stale compiled DLLs for a mod (keep currentDll), making re-apply idempotent.</summary>
    public void CleanupOldFiles(string modId, string[] architectures, string currentDll)
        => ModFiles.DeleteOld(_engineModsPath, modId, ModFiles.SubfoldersFor(architectures, _arm64), currentDll);

    private void CompileOne(string source, string dllName, string target, string modId, string version, string[] extraArgs)
    {
        string clang = Path.Combine(_compilerPath, "bin", "clang++.exe");
        string sub = Sub(target);
        string engineLib = Path.Combine(_enginePath, sub, "windhawk.lib");
        string outDll = Path.Combine(_engineModsPath, sub, dllName);
        Directory.CreateDirectory(Path.GetDirectoryName(outDll)!);

        string idDef = "-DWH_MOD_ID=L\"" + modId.Replace("\"", "\\\"") + "\"";
        string verDef = "-DWH_MOD_VERSION=L\"" + version.Replace("\"", "\\\"") + "\"";

        var args = new List<string>
        {
            "-std=c++23", "-O2", "-shared", "-DUNICODE", "-D_UNICODE",
            "-DWINVER=0x0A00", "-D_WIN32_WINNT=0x0A00", "-D_WIN32_IE=0x0A00", "-DNTDDI_VERSION=0x0A000008",
            "-D__USE_MINGW_ANSI_STDIO=0", "-DWH_MOD", idDef, verDef,
            engineLib, "-x", "c++", "-", "-include", "windhawk_api.h",
            "-target", target, "-Wl,--export-all-symbols", "-o", outDll,
        };
        args.AddRange(extraArgs);

        var psi = new ProcessStartInfo(clang)
        {
            WorkingDirectory = _compilerPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start clang++");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        // Mod source is fed via stdin as UTF-8 (no BOM), matching the UI.
        var bytes = new UTF8Encoding(false).GetBytes(source);
        proc.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
        proc.StandardInput.Close();

        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            string err = stderr.GetAwaiter().GetResult();
            throw new InvalidOperationException(
                $"Compilation failed for {target} (exit 0x{proc.ExitCode:X}):\n{err}");
        }
    }

    /// <summary>Copy the runtime libs a compiled mod needs into the engine mods folder.</summary>
    private void CopyCompilerLibs(string target)
    {
        string libsDir = Path.Combine(_compilerPath, target, "bin");
        string destDir = Path.Combine(_engineModsPath, Sub(target));
        if (!Directory.Exists(libsDir)) return; // compiler for this target not present
        Directory.CreateDirectory(destDir);

        (string from, string to)[] files =
        [
            ("libc++.dll", "libc++.whl"),
            ("libunwind.dll", "libunwind.whl"),
            ("windhawk-mod-shim.dll", "windhawk-mod-shim.dll"),
        ];

        foreach (var (from, to) in files)
        {
            string src = Path.Combine(libsDir, from);
            string dst = Path.Combine(destDir, to);
            if (!File.Exists(src)) continue;
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) == File.GetLastWriteTimeUtc(src)) continue;
            try { File.Copy(src, dst, true); }
            catch when (File.Exists(dst)) { /* in use; leave existing copy */ }
        }
    }

    /// <summary>Minimal port of the splitargs helper used for @compilerOptions.</summary>
    private static string[] SplitArgs(string input)
    {
        var ret = new List<string>();
        var buf = new StringBuilder();
        bool sq = false, dq = false;
        foreach (char c in input)
        {
            if (c == '\'' && !dq) { sq = !sq; continue; }
            if (c == '"' && !sq) { dq = !dq; continue; }
            if (!sq && !dq && char.IsWhiteSpace(c))
            {
                if (buf.Length > 0) { ret.Add(buf.ToString()); buf.Clear(); }
            }
            else buf.Append(c);
        }
        if (buf.Length > 0) ret.Add(buf.ToString());
        return ret.ToArray();
    }
}
