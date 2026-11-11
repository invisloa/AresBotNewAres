using DriverScanTester.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DriverScanTester.Services
{
    internal readonly record struct AresLauncherWindowInfo(
        nint Handle,
        uint ProcessId,
        string Title,
        Rectangle WindowScreenBounds,
        Rectangle ClientScreenBounds);

    /// <summary>
    /// Finds and captures the launcher by process identity, and converts window client
    /// coordinates to physical desktop pixels under per-monitor DPI awareness.
    /// </summary>
    internal sealed class AresLauncherWindowService
    {
        private const string LauncherProcessName = "Launcher";
        private const string GameProcessName = "Ares";
        internal const int PlayButtonReferenceClientWidth = 1216;
        internal const int PlayButtonReferenceClientHeight = 699;
        internal const int PlayButtonRightOffset = 135;
        internal const int PlayButtonBottomOffset = 81;
        private const double PlayButtonReferenceAspectRatio = 1216.0 / 699.0;
        private const double PlayButtonAspectRatioTolerance = 0.04;
        private const int SwRestore = 9;
        private const uint GaRoot = 2;
        private static readonly nint PerMonitorAwareV2 = new(-4);

        private delegate bool EnumWindowsCallback(nint hwnd, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(nint hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(nint hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(nint hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hwnd);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern nint GetAncestor(nint hwnd, uint flags);

        private sealed class DpiAwarenessScope : IDisposable
        {
            private readonly nint _previous;

            private DpiAwarenessScope(nint previous) => _previous = previous;

            public static bool TryEnter(out DpiAwarenessScope? scope)
            {
                nint previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
                if (previous == nint.Zero)
                {
                    scope = null;
                    return false;
                }

                scope = new DpiAwarenessScope(previous);
                return true;
            }

            public void Dispose() => SetThreadDpiAwarenessContext(_previous);
        }

        public bool TryFindLauncherWindow(
            int preferredProcessId,
            out AresLauncherWindowInfo window,
            out string reason)
        {
            window = default;
            reason = string.Empty;
            if (!DpiAwarenessScope.TryEnter(out DpiAwarenessScope? dpiScope))
            {
                reason = $"Could not set per-monitor DPI awareness (Win32Error={Marshal.GetLastWin32Error()}).";
                return false;
            }

            using (dpiScope)
            {
                HashSet<int> knownLauncherProcessIds = CaptureLauncherProcessIds(out string processError);
                HashSet<uint> launcherPids = new(knownLauncherProcessIds.Select(pid => (uint)pid));

                if (launcherPids.Count == 0)
                {
                    reason = processError.Length > 0
                        ? $"Could not enumerate Launcher.exe processes: {processError}"
                        : "No Launcher.exe process is running.";
                    return false;
                }

                AresLauncherWindowInfo? best = null;
                nint foregroundWindow = GetForegroundWindow();
                nint foregroundRoot = foregroundWindow == nint.Zero
                    ? nint.Zero
                    : GetAncestor(foregroundWindow, GaRoot);
                bool bestIsForeground = false;
                bool bestIsPreferredProcess = false;
                long bestArea = 0;
                EnumWindows(
                    (hwnd, _) =>
                    {
                        if (!IsWindowVisible(hwnd) || !TryReadWindow(hwnd, out AresLauncherWindowInfo candidate))
                            return true;

                        if (!launcherPids.Contains(candidate.ProcessId))
                            return true;

                        bool isForeground = hwnd == foregroundWindow || hwnd == foregroundRoot;
                        bool isPreferredProcess = candidate.ProcessId == (uint)Math.Max(0, preferredProcessId);
                        long area = (long)candidate.ClientScreenBounds.Width * candidate.ClientScreenBounds.Height;
                        bool isBetterCandidate = best is null ||
                            (isForeground && !bestIsForeground) ||
                            (isForeground == bestIsForeground &&
                             (isPreferredProcess && !bestIsPreferredProcess ||
                              (isPreferredProcess == bestIsPreferredProcess && area > bestArea)));
                        if (isBetterCandidate)
                        {
                            best = candidate;
                            bestIsForeground = isForeground;
                            bestIsPreferredProcess = isPreferredProcess;
                            bestArea = area;
                        }

                        return true;
                    },
                    nint.Zero);

                if (best is null)
                {
                    reason = $"No visible, non-empty top-level window was found for Launcher.exe PID(s) {string.Join(", ", launcherPids)}.";
                    return false;
                }

                window = best.Value;
                return true;
            }
        }

        public HashSet<int> CaptureLauncherProcessIds() => CaptureLauncherProcessIds(out _);

        private static HashSet<int> CaptureLauncherProcessIds(out string error)
        {
            error = string.Empty;
            var processIds = new HashSet<int>();
            try
            {
                foreach (Process process in Process.GetProcessesByName(LauncherProcessName))
                {
                    using (process)
                        processIds.Add(process.Id);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return processIds;
        }

        public static bool TryCalculatePlayButtonPosition(
            Rectangle clientScreenBounds,
            out Point screenPosition,
            out string reason)
        {
            screenPosition = Point.Empty;
            reason = string.Empty;
            if (clientScreenBounds.Width <= 0 || clientScreenBounds.Height <= 0)
            {
                reason = $"The launcher client area is empty ({clientScreenBounds.Width}x{clientScreenBounds.Height}).";
                return false;
            }

            double aspectRatio = clientScreenBounds.Width / (double)clientScreenBounds.Height;
            if (Math.Abs(aspectRatio - PlayButtonReferenceAspectRatio) > PlayButtonAspectRatioTolerance)
            {
                reason = $"Unsupported launcher client aspect ratio {aspectRatio:F3}; the measured PLAY layout is approximately 16:9.";
                return false;
            }

            // The launcher diagnostic capture measured a 1216x699 client with PLAY
            // 135 px from the right and 81 px from the bottom. Scale the observed client
            // point with live dimensions, then derive edge offsets for DPI-scaled clients.
            int clientX = (int)Math.Round(
                (PlayButtonReferenceClientWidth - PlayButtonRightOffset) *
                (double)clientScreenBounds.Width / PlayButtonReferenceClientWidth,
                MidpointRounding.AwayFromZero);
            int clientY = (int)Math.Round(
                (PlayButtonReferenceClientHeight - PlayButtonBottomOffset) *
                (double)clientScreenBounds.Height / PlayButtonReferenceClientHeight,
                MidpointRounding.AwayFromZero);
            int rightOffset = clientScreenBounds.Width - clientX;
            int bottomOffset = clientScreenBounds.Height - clientY;

            int clickX = clientScreenBounds.Left + clientScreenBounds.Width - rightOffset;
            int clickY = clientScreenBounds.Top + clientScreenBounds.Height - bottomOffset;
            var candidate = new Point(clickX, clickY);
            if (!clientScreenBounds.Contains(candidate))
            {
                reason = $"Calculated PLAY point ({clickX},{clickY}) falls outside client bounds {clientScreenBounds}.";
                return false;
            }

            screenPosition = candidate;
            return true;
        }

        public bool TryFocusLauncherWindow(AresLauncherWindowInfo window, out string reason)
        {
            reason = string.Empty;
            if (!DpiAwarenessScope.TryEnter(out DpiAwarenessScope? dpiScope))
            {
                reason = $"Could not set per-monitor DPI awareness (Win32Error={Marshal.GetLastWin32Error()}).";
                return false;
            }

            using (dpiScope)
            {
                if (!IsWindow(window.Handle) || !IsWindowVisible(window.Handle))
                {
                    reason = "The launcher window was closed or hidden before activation.";
                    return false;
                }

                ShowWindow(window.Handle, SwRestore);
                BringWindowToTop(window.Handle);
                SetForegroundWindow(window.Handle);
                nint foreground = GetForegroundWindow();
                if (foreground != window.Handle && GetAncestor(foreground, GaRoot) != window.Handle)
                {
                    reason = $"Windows did not bring launcher window 0x{window.Handle.ToInt64():X} to the foreground.";
                    return false;
                }

                if (!TryReadWindow(window.Handle, out AresLauncherWindowInfo current) ||
                    current.ProcessId != window.ProcessId)
                {
                    reason = "The launcher window no longer belongs to the detected process or has no usable client area.";
                    return false;
                }

                return true;
            }
        }

        public bool TryFocusGameWindow(AresLauncherWindowInfo window, out string reason)
        {
            reason = string.Empty;
            if (!DpiAwarenessScope.TryEnter(out DpiAwarenessScope? dpiScope))
            {
                reason = $"Could not set per-monitor DPI awareness (Win32Error={Marshal.GetLastWin32Error()}).";
                return false;
            }

            using (dpiScope)
            {
                if (!IsWindow(window.Handle) || !IsWindowVisible(window.Handle))
                {
                    reason = "The Ares game window was closed or hidden before login input.";
                    return false;
                }

                ShowWindow(window.Handle, SwRestore);
                BringWindowToTop(window.Handle);
                SetForegroundWindow(window.Handle);
                nint foreground = GetForegroundWindow();
                if (foreground != window.Handle && GetAncestor(foreground, GaRoot) != window.Handle)
                {
                    reason = "Windows did not bring the Ares game window to the foreground.";
                    return false;
                }

                if (!TryReadWindow(window.Handle, out AresLauncherWindowInfo current) ||
                    current.ProcessId != window.ProcessId)
                {
                    reason = "The game window changed before login input.";
                    return false;
                }

                try
                {
                    using Process process = Process.GetProcessById((int)current.ProcessId);
                    if (!string.Equals(process.ProcessName, GameProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "The selected foreground window no longer belongs to Ares.exe.";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    reason = $"Could not verify the Ares game process before login input: {ex.Message}";
                    return false;
                }

                return true;
            }
        }

        public bool TryRefreshLauncherWindow(
            AresLauncherWindowInfo previous,
            out AresLauncherWindowInfo current,
            out string reason)
        {
            current = default;
            reason = string.Empty;
            if (!DpiAwarenessScope.TryEnter(out DpiAwarenessScope? dpiScope))
            {
                reason = $"Could not set per-monitor DPI awareness (Win32Error={Marshal.GetLastWin32Error()}).";
                return false;
            }

            using (dpiScope)
            {
                if (!IsWindow(previous.Handle) || !IsWindowVisible(previous.Handle) ||
                    !TryReadWindow(previous.Handle, out AresLauncherWindowInfo refreshed))
                {
                    reason = "The launcher window was closed, hidden, or lost its client area before clicking.";
                    return false;
                }

                if (refreshed.ProcessId != previous.ProcessId ||
                    !CaptureLauncherProcessIds().Contains((int)refreshed.ProcessId))
                {
                    reason = "The launcher HWND no longer belongs to a running Launcher.exe process.";
                    return false;
                }

                current = refreshed;
                return true;
            }
        }

        public Bitmap? CaptureClientArea(
            AresLauncherWindowInfo window,
            out Rectangle clientScreenBounds,
            out string reason)
        {
            clientScreenBounds = Rectangle.Empty;
            reason = string.Empty;
            if (!DpiAwarenessScope.TryEnter(out DpiAwarenessScope? dpiScope))
            {
                reason = $"Could not set per-monitor DPI awareness (Win32Error={Marshal.GetLastWin32Error()}).";
                return null;
            }

            using (dpiScope)
            {
                if (!IsWindow(window.Handle) || !IsWindowVisible(window.Handle) ||
                    !TryReadWindow(window.Handle, out AresLauncherWindowInfo current) ||
                    current.ProcessId != window.ProcessId ||
                    !CaptureLauncherProcessIds().Contains((int)current.ProcessId))
                {
                    reason = "The launcher window was closed, hidden, or no longer belongs to the detected Launcher.exe process.";
                    return null;
                }

                string captureReason = string.Empty;
                Bitmap? frame = ScreenshotService.CaptureWindowClientArea(
                    window.Handle,
                    out clientScreenBounds,
                    detail => captureReason = detail);
                if (frame == null && reason.Length == 0)
                    reason = captureReason.Length == 0
                        ? "The launcher client area could not be captured."
                        : captureReason;
                return frame;
            }
        }

        public bool TryLeftClick(int screenX, int screenY, int delayMs = 100)
            => TryLeftClick(screenX, screenY, delayMs, out _);

        public bool TryLeftClick(int screenX, int screenY, int delayMs, out string failureReason)
        {
            if (!DpiAwarenessScope.TryEnter(out DpiAwarenessScope? dpiScope))
            {
                failureReason = $"Could not set per-monitor DPI awareness (Win32Error={Marshal.GetLastWin32Error()}).";
                return false;
            }

            using (dpiScope)
                return MouseOperations.TryMoveAndLeftClickAbsolute(screenX, screenY, delayMs, out failureReason);
        }

        public HashSet<int> CaptureGameProcessIds() =>
            TryCaptureGameProcessIds(out HashSet<int> processIds, out _) ? processIds : new HashSet<int>();

        public bool TryCaptureGameProcessIds(out HashSet<int> processIds, out string reason)
        {
            processIds = new HashSet<int>();
            reason = string.Empty;
            try
            {
                foreach (Process process in Process.GetProcessesByName(GameProcessName))
                {
                    using (process)
                        processIds.Add(process.Id);
                }
            }
            catch (Exception ex)
            {
                reason = $"Could not enumerate Ares.exe processes: {ex.Message}";
                return false;
            }

            return true;
        }

        public bool HasNewGameProcess(HashSet<int> processIdsBeforeClick) =>
            TryHasNewGameProcess(processIdsBeforeClick, out bool found, out _) && found;

        public bool TryHasNewGameProcess(
            HashSet<int> processIdsBeforeClick,
            out bool found,
            out string reason)
        {
            found = false;
            if (!TryCaptureGameProcessIds(out HashSet<int> processIds, out reason))
                return false;

            found = processIds.Any(processId => !processIdsBeforeClick.Contains(processId));
            return true;
        }

        public bool HasNewVisibleGameWindow(HashSet<int> processIdsBeforeClick)
            => TryHasNewVisibleGameWindow(processIdsBeforeClick, out bool found, out _) && found;

        public bool TryHasNewVisibleGameWindow(
            HashSet<int> processIdsBeforeClick,
            out bool found,
            out string reason)
            => TryFindNewVisibleGameWindow(processIdsBeforeClick, out found, out _, out reason);

        public bool TryFindNewVisibleGameWindow(
            HashSet<int> processIdsBeforeLaunch,
            out bool found,
            out AresLauncherWindowInfo foundWindow,
            out string reason)
        {
            found = false;
            foundWindow = default;
            reason = string.Empty;
            if (!DpiAwarenessScope.TryEnter(out DpiAwarenessScope? dpiScope))
            {
                reason = $"Could not set per-monitor DPI awareness (Win32Error={Marshal.GetLastWin32Error()}).";
                return false;
            }

            using (dpiScope)
            {
                bool windowFound = false;
                AresLauncherWindowInfo windowFoundInfo = default;
                bool enumSucceeded = EnumWindows(
                    (hwnd, _) =>
                    {
                        if (!IsWindowVisible(hwnd) || !TryReadWindow(hwnd, out AresLauncherWindowInfo window))
                            return true;

                        if (processIdsBeforeLaunch.Contains((int)window.ProcessId))
                            return true;

                        try
                        {
                            using Process process = Process.GetProcessById((int)window.ProcessId);
                            if (!windowFound &&
                                string.Equals(process.ProcessName, GameProcessName, StringComparison.OrdinalIgnoreCase) &&
                                window.ClientScreenBounds.Width >= 320 &&
                                window.ClientScreenBounds.Height >= 200)
                            {
                                windowFound = true;
                                windowFoundInfo = window;
                            }
                        }
                        catch
                        {
                            // Process exited while its top-level windows were enumerated.
                        }

                        return true;
                    },
                    nint.Zero);
                found = windowFound;
                foundWindow = windowFoundInfo;
                if (!enumSucceeded)
                    reason = $"EnumWindows failed while verifying Ares startup (Win32Error={Marshal.GetLastWin32Error()}).";
                return enumSucceeded;
            }
        }

        public static bool TryReadWindow(nint hwnd, out AresLauncherWindowInfo window)
        {
            window = default;
            if (!GetWindowRect(hwnd, out NativeRect windowRect) ||
                !GetClientRect(hwnd, out NativeRect clientRect))
            {
                return false;
            }

            NativePoint clientOrigin = new() { X = clientRect.Left, Y = clientRect.Top };
            if (!ClientToScreen(hwnd, ref clientOrigin))
                return false;

            int clientWidth = clientRect.Right - clientRect.Left;
            int clientHeight = clientRect.Bottom - clientRect.Top;
            int windowWidth = windowRect.Right - windowRect.Left;
            int windowHeight = windowRect.Bottom - windowRect.Top;
            if (clientWidth < 200 || clientHeight < 120 || windowWidth <= 0 || windowHeight <= 0)
                return false;

            GetWindowThreadProcessId(hwnd, out uint processId);
            int titleLength = Math.Clamp(GetWindowTextLength(hwnd), 0, 2048);
            var title = new StringBuilder(titleLength + 1);
            GetWindowText(hwnd, title, title.Capacity);

            window = new AresLauncherWindowInfo(
                hwnd,
                processId,
                title.ToString(),
                Rectangle.FromLTRB(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom),
                new Rectangle(clientOrigin.X, clientOrigin.Y, clientWidth, clientHeight));
            return true;
        }
    }
}
