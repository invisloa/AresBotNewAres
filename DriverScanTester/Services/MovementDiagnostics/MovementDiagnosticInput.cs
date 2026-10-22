using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace DriverScanTester.Services
{
    public readonly record struct MovementDiagnosticInputCallResult(
        bool ApiSucceeded,
        uint InsertedCount,
        int Win32Error,
        string Detail);

    public interface IMovementDiagnosticKeyboardBackend : IDisposable
    {
        MovementDiagnosticInputMode Mode { get; }
        string Name { get; }
        MovementDiagnosticInputCallResult KeyDown(byte virtualKey, byte scanCode);
        MovementDiagnosticInputCallResult KeyUp(byte virtualKey, byte scanCode);
    }

    /// <summary>
    /// Diagnostic-only keybd_event backend. Production keeps using GameInput.keybd_event;
    /// this wrapper exists so the experiment records its own calls without changing the bot.
    /// keybd_event returns void, so Windows exposes no insertion count for this API.
    /// </summary>
    internal sealed class KeybdEventDiagnosticKeyboardBackend : IMovementDiagnosticKeyboardBackend
    {
        public MovementDiagnosticInputMode Mode => MovementDiagnosticInputMode.KeybdEvent;
        public string Name => "keybd_event";

        public MovementDiagnosticInputCallResult KeyDown(byte virtualKey, byte scanCode)
        {
            GameInput.keybd_event(virtualKey, scanCode, 0, 0);
            return new MovementDiagnosticInputCallResult(
                true,
                1,
                0,
                "keybd_event returned void; no insertion count is available from this API.");
        }

        public MovementDiagnosticInputCallResult KeyUp(byte virtualKey, byte scanCode)
        {
            GameInput.keybd_event(virtualKey, scanCode, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            return new MovementDiagnosticInputCallResult(
                true,
                1,
                0,
                "keybd_event key-up returned void; no insertion count is available from this API.");
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Diagnostic-only SendInput implementation. It is deliberately private to the
    /// diagnostic system and never replaces GameInput.keybd_event in production code.
    /// </summary>
    internal sealed class SendInputDiagnosticKeyboardBackend : IMovementDiagnosticKeyboardBackend
    {
        private const uint InputKeyboard = 1;
        internal const uint KeyEventKeyUp = 0x0002;
        internal const uint KeyEventScanCode = 0x0008;

        public SendInputDiagnosticKeyboardBackend(MovementDiagnosticInputMode mode)
        {
            if (mode != MovementDiagnosticInputMode.SendInputVirtualKey &&
                mode != MovementDiagnosticInputMode.SendInputScanCode)
                throw new ArgumentOutOfRangeException(nameof(mode), "SendInput backend supports virtual-key or scan-code mode.");

            Mode = mode;
        }

        public MovementDiagnosticInputMode Mode { get; }
        public string Name => Mode == MovementDiagnosticInputMode.SendInputVirtualKey
            ? "SendInput (virtual key)"
            : "SendInput (scan code)";

        public MovementDiagnosticInputCallResult KeyDown(byte virtualKey, byte scanCode)
            => Send(virtualKey, scanCode, keyUp: false);

        public MovementDiagnosticInputCallResult KeyUp(byte virtualKey, byte scanCode)
            => Send(virtualKey, scanCode, keyUp: true);

        private MovementDiagnosticInputCallResult Send(byte virtualKey, byte scanCode, bool keyUp)
        {
            INPUT input = CreateInput(Mode, virtualKey, scanCode, keyUp);
            uint inserted = SendInput(1u, new[] { input }, (uint)Marshal.SizeOf<INPUT>());
            int error = inserted == 1u ? 0 : Marshal.GetLastWin32Error();
            string detail = inserted == 1u
                ? $"SendInput inserted={inserted} events."
                : $@"SendInput inserted={inserted} events; Win32Error={error}.";
            return new MovementDiagnosticInputCallResult(inserted == 1u, inserted, error, detail);
        }

        internal static INPUT CreateInputForTesting(
            MovementDiagnosticInputMode mode,
            byte virtualKey,
            byte scanCode,
            bool keyUp)
            => CreateInput(mode, virtualKey, scanCode, keyUp);

        private static INPUT CreateInput(
            MovementDiagnosticInputMode mode,
            byte virtualKey,
            byte scanCode,
            bool keyUp)
        {
            uint flags = keyUp ? KeyEventKeyUp : 0u;
            ushort wVk;
            ushort wScan;

            if (mode == MovementDiagnosticInputMode.SendInputScanCode)
            {
                flags |= KeyEventScanCode;
                wVk = 0;
                wScan = scanCode;
            }
            else
            {
                wVk = virtualKey;
                wScan = 0;
            }

            return new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = wVk,
                        wScan = wScan,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = 0
                    }
                }
            };
        }

        public void Dispose()
        {
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint cInputs, INPUT[] pInputs, uint cbSize);

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }
    }

    internal static class MovementDiagnosticInputBackendFactory
    {
        public static IMovementDiagnosticKeyboardBackend Create(MovementDiagnosticInputMode mode)
            => mode switch
            {
                MovementDiagnosticInputMode.KeybdEvent => new KeybdEventDiagnosticKeyboardBackend(),
                MovementDiagnosticInputMode.SendInputVirtualKey => new SendInputDiagnosticKeyboardBackend(mode),
                MovementDiagnosticInputMode.SendInputScanCode => new SendInputDiagnosticKeyboardBackend(mode),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), $"No injection backend exists for {mode}.")
            };
    }

    /// <summary>
    /// Tracks only keys this diagnostic actually pressed. ReleaseAll never sends a
    /// blind key-up for a key it does not own.
    /// </summary>
    internal sealed class DiagnosticKeyOwnership
    {
        private readonly object _lock = new();
        private readonly IMovementDiagnosticKeyboardBackend _backend;
        private readonly System.Collections.Generic.Dictionary<MovementDiagnosticKey, bool> _down = new();

        public DiagnosticKeyOwnership(IMovementDiagnosticKeyboardBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public bool IsDown(MovementDiagnosticKey key)
        {
            lock (_lock)
                return _down.TryGetValue(key, out bool down) && down;
        }

        public bool HasOwnedKeys
        {
            get
            {
                lock (_lock)
                    return _down.Values.Any(v => v);
            }
        }

        public MovementDiagnosticInputCallResult SendDown(MovementDiagnosticKey key, bool repeat)
        {
            byte vk = MovementDiagnosticKeyInfo.GetVirtualKey(key);
            byte scan = MovementDiagnosticKeyInfo.GetScanCode(key);

            lock (_lock)
            {
                if (!repeat)
                    _down[key] = true;

                try
                {
                    MovementDiagnosticInputCallResult result = _backend.KeyDown(vk, scan);
                    if (!result.ApiSucceeded)
                        _down[key] = true;
                    return result;
                }
                catch (Exception ex)
                {
                    // The key is recorded as owned before the call so cleanup still attempts
                    // a key-up if the native call failed after partially delivering input.
                    _down[key] = true;
                    return new MovementDiagnosticInputCallResult(
                        false,
                        0,
                        Marshal.GetLastWin32Error(),
                        $"Input API threw: {ex.Message}");
                }
            }
        }

        public MovementDiagnosticInputCallResult SendUp(MovementDiagnosticKey key)
        {
            byte vk = MovementDiagnosticKeyInfo.GetVirtualKey(key);
            byte scan = MovementDiagnosticKeyInfo.GetScanCode(key);

            lock (_lock)
            {
                try
                {
                    MovementDiagnosticInputCallResult result = _backend.KeyUp(vk, scan);
                    if (result.ApiSucceeded)
                        _down[key] = false;
                    return result;
                }
                catch (Exception ex)
                {
                    return new MovementDiagnosticInputCallResult(
                        false,
                        0,
                        Marshal.GetLastWin32Error(),
                        $"Input API threw during key-up: {ex.Message}");
                }
            }
        }

        public IReadOnlyList<(MovementDiagnosticKey Key, MovementDiagnosticInputCallResult Result)> ReleaseAll()
        {
            var results = new List<(MovementDiagnosticKey, MovementDiagnosticInputCallResult)>();
            lock (_lock)
            {
                foreach (var pair in _down.ToList())
                {
                    if (!pair.Value)
                        continue;

                    // One retry: a transient SendInput failure must not leave a synthetic
                    // movement key logically owned after the scenario ends.
                    MovementDiagnosticInputCallResult result = SendUp(pair.Key);
                    if (!result.ApiSucceeded)
                        result = SendUp(pair.Key);

                    results.Add((pair.Key, result));
                    if (!result.ApiSucceeded)
                        _down[pair.Key] = true;
                }
            }
            return results;
        }

        public void DisposeBackend() => _backend.Dispose();
    }

    internal static class MovementDiagnosticKeyInfo
    {
        public static byte GetVirtualKey(MovementDiagnosticKey key) => key switch
        {
            MovementDiagnosticKey.W => GameInput.VK_W,
            MovementDiagnosticKey.A => GameInput.VK_A,
            MovementDiagnosticKey.D => GameInput.VK_D,
            _ => 0
        };

        public static byte GetScanCode(MovementDiagnosticKey key) => key switch
        {
            MovementDiagnosticKey.W => GameInput.SCAN_W,
            MovementDiagnosticKey.A => GameInput.SCAN_A,
            MovementDiagnosticKey.D => GameInput.SCAN_D,
            _ => 0
        };

        public static string GetKeyText(MovementDiagnosticKey key) => key switch
        {
            MovementDiagnosticKey.W => "W",
            MovementDiagnosticKey.A => "A",
            MovementDiagnosticKey.D => "D",
            _ => "?"
        };

        public static string GetVirtualKeyText(MovementDiagnosticKey key)
            => $"0x{GetVirtualKey(key):X2} (VK_{GetKeyText(key)})";

        public static string GetScanCodeText(MovementDiagnosticKey key)
            => $"0x{GetScanCode(key):X2}";
    }
}
