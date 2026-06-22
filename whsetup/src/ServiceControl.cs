using System.Runtime.InteropServices;

namespace Whsetup;

/// <summary>Win32 service install/start/stop/delete via advapi32 (AOT-safe LibraryImport).</summary>
internal static partial class ServiceControl
{
    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_AUTO_START = 0x00000002;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted,
            dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenService(IntPtr scm, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateService(
        IntPtr scm, string serviceName, string displayName, uint desiredAccess,
        uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(IntPtr service, uint numArgs, IntPtr args);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr service, uint control, ref SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(IntPtr service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    public static bool Exists(string name)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw Err("OpenSCManager");
        try
        {
            IntPtr svc = OpenService(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            CloseServiceHandle(svc);
            return true;
        }
        finally { CloseServiceHandle(scm); }
    }

    public static void Create(string name, string displayName, string binaryPath)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw Err("OpenSCManager");
        try
        {
            // serviceStartName = null -> LocalSystem.
            IntPtr svc = CreateService(scm, name, displayName, SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL,
                binaryPath, null, IntPtr.Zero, null, null, null);
            if (svc == IntPtr.Zero) throw Err("CreateService");
            CloseServiceHandle(svc);
        }
        finally { CloseServiceHandle(scm); }
    }

    public static void Start(string name)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw Err("OpenSCManager");
        try
        {
            IntPtr svc = OpenService(scm, name, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero) throw Err("OpenService");
            try
            {
                if (!StartService(svc, 0, IntPtr.Zero))
                {
                    int e = Marshal.GetLastWin32Error();
                    if (e != 1056) throw Err("StartService", e); // 1056 = already running
                }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Stop and delete the service if present (best-effort, for reinstall/uninstall).</summary>
    public static void StopAndDelete(string name)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw Err("OpenSCManager");
        try
        {
            IntPtr svc = OpenService(scm, name, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero) return; // not installed
            try
            {
                var status = new SERVICE_STATUS();
                if (QueryServiceStatus(svc, ref status) && status.dwCurrentState != 1 /*stopped*/)
                {
                    ControlService(svc, SERVICE_CONTROL_STOP, ref status);
                    for (int i = 0; i < 30; i++)
                    {
                        if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == 1) break;
                        Thread.Sleep(500);
                    }
                }
                DeleteService(svc);
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static Exception Err(string what, int? code = null)
        => new InvalidOperationException($"{what} failed (Win32 error {code ?? Marshal.GetLastWin32Error()})");
}
