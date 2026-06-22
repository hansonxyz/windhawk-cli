using System.Runtime.InteropServices;

namespace Whcli;

/// <summary>Minimal Win32 service start/stop control (AOT-safe via LibraryImport).</summary>
internal static partial class ServiceControl
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;

    private const uint STATE_STOPPED = 1;
    private const uint STATE_RUNNING = 4;

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

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(IntPtr svc, uint numArgs, IntPtr args);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr svc, uint control, ref SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr svc, ref SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(IntPtr svc, uint infoLevel, IntPtr buffer, uint bufSize, out uint bytesNeeded);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint ms);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    private const uint SC_STATUS_PROCESS_INFO = 0;
    private const uint SYNCHRONIZE = 0x00100000;

    // PID of the running service process (0 if stopped). SERVICE_STATUS_PROCESS is 9 uints;
    // dwProcessId is the 8th field at byte offset 28 — read it directly (AOT-friendly).
    private static uint GetServicePid(IntPtr svc)
    {
        const int size = 36;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, buf, size, out _)) return 0;
            return (uint)Marshal.ReadInt32(buf, 28);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static IntPtr Open(string name, uint access, out string error)
    {
        error = "";
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) { error = "cannot open service manager (need admin?)"; return IntPtr.Zero; }
        IntPtr svc = OpenService(scm, name, access | SERVICE_QUERY_STATUS);
        CloseServiceHandle(scm);
        if (svc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            error = err == 1060 ? $"service '{name}' is not installed"
                  : err == 5 ? "access denied (run elevated)"
                  : $"OpenService failed ({err})";
        }
        return svc;
    }

    // Wait for RUNNING. Returns: 1 = running, 0 = terminated (went back to stopped after a
    // start attempt — e.g. the transient access-denied on a too-quick restart), -1 = timeout.
    private static int WaitForRunning(IntPtr svc, int timeoutMs)
    {
        var status = new SERVICE_STATUS();
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (!QueryServiceStatus(svc, ref status)) return -1;
            if (status.dwCurrentState == STATE_RUNNING) return 1;
            if (status.dwCurrentState == STATE_STOPPED) return 0; // pending -> stopped = it terminated
            System.Threading.Thread.Sleep(300);
            waited += 300;
        }
        return -1;
    }

    // Startup can be slow when AutomaticUpdates runs a pre-engine-start mod update inside the
    // service before it signals RUNNING, so allow generous time. Retry if the service
    // terminates during start (transient access-denied while the prior instance is still
    // releasing its global engine objects after a stop).
    /// <summary>Start the service; returns true if running afterwards. Already-running is success.</summary>
    public static bool Start(string name, out string error, int timeoutMs = 180000)
    {
        error = "";
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            IntPtr svc = Open(name, SERVICE_START, out error);
            if (svc == IntPtr.Zero) return false;
            try
            {
                var status = new SERVICE_STATUS();
                if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == STATE_RUNNING)
                    return true;
                if (!StartService(svc, 0, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 1056) return true; // already running
                    error = err == 5 ? "access denied (run elevated)" : $"StartService failed ({err})";
                    if (err != 5) return false;
                    // access denied here is usually transient right after a stop — retry
                }
                else
                {
                    int r = WaitForRunning(svc, timeoutMs);
                    if (r == 1) return true;
                    error = r == 0 ? "service terminated during start (will retry)" : "service did not reach running state in time";
                    if (r == -1) return false; // genuine timeout, not a quick-fail; don't loop for minutes
                }
            }
            finally { CloseServiceHandle(svc); }

            System.Threading.Thread.Sleep(3000 * attempt); // back off and let the old instance finish cleanup
        }
        return false;
    }

    /// <summary>
    /// Stop the service and wait until it has FULLY stopped — i.e. SCM reports STOPPED and
    /// the service process has actually exited (releasing the engine's Global\ objects), so a
    /// subsequent start won't hit the transient access-denied. Already-stopped is success.
    /// </summary>
    public static bool Stop(string name, out string error, int timeoutMs = 180000)
    {
        IntPtr svc = Open(name, SERVICE_STOP, out error);
        if (svc == IntPtr.Zero) return false;
        try
        {
            var status = new SERVICE_STATUS();
            if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == STATE_STOPPED)
                return true;

            // Take a handle to the live process now so we can wait for its real exit later.
            uint pid = GetServicePid(svc);
            IntPtr hProc = pid != 0 ? OpenProcess(SYNCHRONIZE, false, pid) : IntPtr.Zero;
            try
            {
                if (!ControlService(svc, SERVICE_CONTROL_STOP, ref status))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1062 /* not started */) { error = $"stop failed ({err})"; return false; }
                }

                // Poll SCM until it's actually STOPPED (not merely STOP_PENDING).
                int waited = 0;
                while (waited < timeoutMs)
                {
                    if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == STATE_STOPPED) break;
                    System.Threading.Thread.Sleep(300);
                    waited += 300;
                }
                if (status.dwCurrentState != STATE_STOPPED) { error = "service did not stop in time"; return false; }

                // Then wait for the old process to truly exit so its kernel objects are freed.
                if (hProc != IntPtr.Zero)
                    WaitForSingleObject(hProc, (uint)Math.Max(2000, timeoutMs - waited));
                return true;
            }
            finally { if (hProc != IntPtr.Zero) CloseHandle(hProc); }
        }
        finally { CloseServiceHandle(svc); }
    }

    /// <summary>Stop (if running) then start. Valid from a stopped state (just starts).</summary>
    public static bool Restart(string name, out string error, int timeoutMs = 180000)
    {
        if (ServiceQuery.State(name) == "running" && !Stop(name, out error, timeoutMs)) return false;
        return Start(name, out error, timeoutMs);
    }
}
