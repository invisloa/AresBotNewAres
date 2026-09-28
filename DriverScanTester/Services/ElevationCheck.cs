using System;
using System.Runtime.InteropServices;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Detects whether a process runs elevated (as administrator).
    ///
    /// Why this matters: Windows UIPI (User Interface Privilege Isolation) silently
    /// blocks synthetic input (keybd_event / SendInput) sent from a non-elevated
    /// process to a window owned by an elevated process. The classic symptom in
    /// this bot is a game that turns its camera (memory writes via the kernel
    /// driver still work) but never moves, and potions that are never consumed —
    /// because W/A/D/1/2/3 never reach the game. Fix: run the bot as administrator.
    /// </summary>
    internal static class ElevationCheck
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int infoLength, out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>True when the current process is running elevated.</summary>
        public static bool IsCurrentProcessElevated()
        {
            return IsProcessElevated(Environment.ProcessId);
        }

        /// <summary>True when the process with the given PID is running elevated.
        /// Returns false when the token cannot be queried.</summary>
        public static bool IsProcessElevated(int pid)
        {
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
                return false;

            try
            {
                if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token))
                    return false;

                try
                {
                    if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _))
                        return false;

                    return elevated != 0;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }
    }
}
