using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DriverScanTester.Utils;

namespace DriverScanTester.Services
{
    public readonly record struct MovementDiagnosticMouseAssistResult(
        bool Applied,
        bool GameForeground,
        bool CursorInsideClient,
        int? CursorX,
        int? CursorY,
        int? TargetX,
        int? TargetY,
        string Detail);

    public interface IMovementDiagnosticMouseTurnAssist : IDisposable
    {
        bool IsActive { get; }
        bool IsMovingCursor { get; }
        MovementDiagnosticMouseAssistResult Acquire(bool leftSide, string stageLabel);
        MovementDiagnosticMouseAssistResult Release(string reason);
        MovementDiagnosticMouseAssistResult Cleanup(string reason);
    }

    /// <summary>
    /// Diagnostic-only edge-cursor assistance. It mirrors the proven ownership rules of
    /// MovementSystem's KeyboardTurn fast-turn helper: only act while the game is
    /// foreground, only touch a cursor already inside the game client, use the same edge
    /// margin/neutral helpers, and never fight a cursor the human moved away.
    /// It never runs while MovementSystem is active — the diagnostic rejects that state.
    /// </summary>
    internal sealed class MovementDiagnosticMouseTurnAssist : IMovementDiagnosticMouseTurnAssist
    {
        private readonly uint _pid;
        private readonly Action<string> _log;
        private readonly object _lock = new();
        private bool _active;
        private bool _leftSide;
        private int _targetX;
        private int _targetY;
        private int _neutralX;

        public MovementDiagnosticMouseTurnAssist(uint pid, Action<string> log)
        {
            _pid = pid;
            _log = log;
        }

        public bool IsActive
        {
            get { lock (_lock) return _active; }
        }

        public bool IsMovingCursor => IsActive;

        public MovementDiagnosticMouseAssistResult Acquire(bool leftSide, string stageLabel)
        {
            lock (_lock)
            {
                if (!TryResolveClient(out nint hwnd, out KeyboardFastTurnClient client, out string failure))
                    return Fail(failure);

                bool foreground = IsGameForeground(client.Hwnd);
                if (!foreground)
                    return Fail("game window is not in the foreground");

                if (!MouseOperations.TryGetCursorPosition(out int cursorX, out int cursorY))
                    return Fail("GetCursorPos failed; cursor ownership not taken");

                if (!MovementSystem.IsCursorInsideKeyboardFastTurnClient(
                        client.Left, client.Top, client.Right, client.Bottom, cursorX, cursorY))
                    return Fail($"cursor ({cursorX},{cursorY}) is outside the game client");

                int edgeX = MovementSystem.ComputeKeyboardFastTurnEdgeX(
                    client.Left,
                    client.Right - client.Left + 1,
                    leftSide,
                    BotConstants.Movement.KeyboardFastTurnMouseEdgeMarginPx);
                int targetY = ResolveCursorY(client, cursorY);
                int neutralX = MovementSystem.ComputeKeyboardFastTurnNeutralX(
                    client.Left,
                    client.Right - client.Left + 1);

                SetCursorWithoutSynchronousLogging(edgeX, targetY);
                bool readBack = MouseOperations.TryGetCursorPosition(out int actualX, out int actualY);
                bool atTarget = readBack &&
                    Math.Abs(actualX - edgeX) <= BotConstants.Movement.KeyboardFastTurnMouseReassertTolerancePx &&
                    Math.Abs(actualY - targetY) <= BotConstants.Movement.KeyboardFastTurnMouseReassertTolerancePx;

                if (!atTarget)
                {
                    return Fail(readBack
                        ? $"cursor did not reach the requested edge ({actualX},{actualY} vs {edgeX},{targetY})"
                        : "cursor position readback failed after SetCursorPos");
                }

                _active = true;
                _leftSide = leftSide;
                _targetX = edgeX;
                _targetY = targetY;
                _neutralX = neutralX;

                _log($"[MovementDiag][MouseAssist] {stageLabel}: cursor -> ({edgeX},{targetY}) " +
                     $"from ({cursorX},{cursorY}), side={(leftSide ? "left" : "right")}, hwnd=0x{client.Hwnd:X}.");

                return new MovementDiagnosticMouseAssistResult(
                    true, true, true, cursorX, cursorY, edgeX, targetY,
                    $"edge acquired at ({edgeX},{targetY}); neutral target x={neutralX}");
            }
        }

        public MovementDiagnosticMouseAssistResult Release(string reason) => Cleanup(reason);

        public MovementDiagnosticMouseAssistResult Cleanup(string reason)
        {
            lock (_lock)
            {
                if (!_active)
                    return new MovementDiagnosticMouseAssistResult(
                        false, true, true, null, null, null, null, "no diagnostic cursor assistance was active");

                _active = false;
                bool read = MouseOperations.TryGetCursorPosition(out int cursorX, out int cursorY);
                if (!read)
                {
                    _log($"[MovementDiag][MouseAssist] {reason}: cleanup could not read cursor; leaving it untouched.");
                    return new MovementDiagnosticMouseAssistResult(
                        false, false, true, null, null, null, null, "GetCursorPos failed during cleanup; cursor left untouched");
                }

                bool stillAtOwnedEdge =
                    Math.Abs(cursorX - _targetX) <= BotConstants.Movement.KeyboardFastTurnMouseReassertTolerancePx;

                if (!stillAtOwnedEdge)
                {
                    _log($"[MovementDiag][MouseAssist] {reason}: cursor moved by the user to ({cursorX},{cursorY}); " +
                         "leaving it untouched.");
                    return new MovementDiagnosticMouseAssistResult(
                        false, true, true, cursorX, cursorY, null, null,
                        "cursor no longer at the diagnostic-owned edge; human movement respected");
                }

                SetCursorWithoutSynchronousLogging(_neutralX, _targetY);
                bool readBack = MouseOperations.TryGetCursorPosition(out int actualX, out int actualY);
                _log($"[MovementDiag][MouseAssist] {reason}: cursor returned to neutral ({_neutralX},{_targetY}); " +
                     $"readback={(readBack ? $"({actualX},{actualY})" : "failed")}.");
                return new MovementDiagnosticMouseAssistResult(
                    true, true, true, actualX, actualY, _neutralX, _targetY,
                    readBack ? "cursor returned to client-centre x" : "neutral move issued; readback failed");
            }
        }

        public void Dispose() => Cleanup("diagnostic mouse assistance disposed");

        /// <summary>
        /// MouseOperations.Log writes synchronously to the application log. Suppress it
        /// for the one or two diagnostic cursor moves so file I/O cannot stretch the
        /// bounded experiment stages. The diagnostic records its own mouse events.
        /// </summary>
        private static void SetCursorWithoutSynchronousLogging(int x, int y)
        {
            Action<string>? previous = MouseOperations.Log;
            MouseOperations.Log = null;
            try
            {
                MouseOperations.SetCursorPositionAbsolute(x, y);
            }
            finally
            {
                MouseOperations.Log = previous;
            }
        }

        private MovementDiagnosticMouseAssistResult Fail(string detail)
        {
            _log($"[MovementDiag][MouseAssist] Assistance not acquired: {detail}.");
            return new MovementDiagnosticMouseAssistResult(false, false, false, null, null, null, null, detail);
        }

        private static int ResolveCursorY(KeyboardFastTurnClient client, int fallbackY)
        {
            int margin = BotConstants.Movement.KeyboardFastTurnMouseEdgeMarginPx;
            if (fallbackY >= client.Top + margin && fallbackY < client.Bottom - margin)
                return fallbackY;
            return (client.Top + client.Bottom) / 2;
        }

        private bool TryResolveClient(out nint hwnd, out KeyboardFastTurnClient client, out string failure)
        {
            hwnd = nint.Zero;
            client = default;
            failure = "";

            try
            {
                var process = Process.GetProcessById((int)_pid);
                if (process == null || process.HasExited)
                {
                    failure = "game process is not available";
                    return false;
                }

                hwnd = process.MainWindowHandle;
                if (hwnd == nint.Zero)
                    hwnd = FindWindow(null, "Legend of Ares");
                if (hwnd == nint.Zero)
                    hwnd = FindWindow(null, "Nostalgia");
                if (hwnd == nint.Zero)
                    hwnd = FindWindow(null, "Epic Of Ares Client");
                if (hwnd == nint.Zero)
                {
                    failure = "game window handle could not be resolved";
                    return false;
                }

                if (!GetClientRect(hwnd, out RECT rect))
                {
                    failure = "GetClientRect failed";
                    return false;
                }

                var origin = new POINT { X = 0, Y = 0 };
                if (!ClientToScreen(hwnd, ref origin))
                {
                    failure = "ClientToScreen failed";
                    return false;
                }

                client = new KeyboardFastTurnClient(
                    hwnd,
                    origin.X,
                    origin.Y,
                    origin.X + rect.Right - rect.Left - 1,
                    origin.Y + rect.Bottom - rect.Top - 1);
                return true;
            }
            catch (Exception ex)
            {
                failure = $"game client resolution failed: {ex.Message}";
                return false;
            }
        }

        private static bool IsGameForeground(nint gameHwnd)
        {
            nint foreground = GetForegroundWindow();
            return foreground != nint.Zero && foreground == gameHwnd;
        }

        private readonly struct KeyboardFastTurnClient
        {
            public KeyboardFastTurnClient(nint hwnd, int left, int top, int right, int bottom)
            {
                Hwnd = hwnd;
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public nint Hwnd { get; }
            public int Left { get; }
            public int Top { get; }
            public int Right { get; }
            public int Bottom { get; }
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
