using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Whcli;

/// <summary>
/// Authenticode verification for self-update: a downloaded file is accepted only if it
/// has a VALID signature (WinVerifyTrust) AND the signer certificate's thumbprint matches
/// our pinned signing cert. This prevents a compromised release feed from pushing an
/// unsigned or foreign-signed update.
/// </summary>
internal static partial class Signature
{
    public static bool IsSignedBy(string path, string expectedThumbprint)
    {
        if (!File.Exists(path)) return false;
        if (!VerifyTrust(path)) return false;          // valid, untampered Authenticode signature
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return string.Equals(cert.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool VerifyTrust(string path)
    {
        // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        IntPtr pPath = Marshal.StringToHGlobalUni(path);
        IntPtr pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pPath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // WTD_REVOKE_NONE
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pUnion = pFileInfo,
                dwStateAction = 0,
            };
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0; // 0 == trusted
        }
        finally
        {
            Marshal.FreeHGlobal(pFileInfo);
            Marshal.FreeHGlobal(pPath);
        }
    }

    // Blittable layouts (no managed strings) so they work under Native AOT.
    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion; // pFile (we only use WTD_CHOICE_FILE)
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
