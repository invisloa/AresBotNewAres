using DriverScanTester.Models;
using DriverScanTester.Utils;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;

namespace DriverScanTester.ViewModels
{
    // Added CLong (32-bit) alongside existing options
    public enum LockValueType { Byte, Short, CLong, Long }

    public enum LockPriority { Low, Mid, High }

    public enum LockGroup { Camera, Attack, Skill, Random }

    public sealed class LockAddressEntryViewModel : BaseViewModel
    {
        // Dependencies provided by parent:
        private readonly Func<bool> _isAttached;
        private readonly Func<uint> _getPid;
        private readonly LockingValuesViewModel.DriverReadDelegate? _driverRead;
        private readonly LockingValuesViewModel.DriverWriteDelegate _driverWrite;
        private readonly Func<int> _getPointerSize;
        private readonly Func<string, ulong> _getModuleBase;
        private readonly Action<string> _appendLog;

        public LockAddressEntryViewModel(
            Func<bool> isAttached,
            Func<uint> getPid,
            LockingValuesViewModel.DriverReadDelegate? driverRead,
            LockingValuesViewModel.DriverWriteDelegate driverWrite,
            Func<int> getPointerSize,
            Func<string, ulong> getModuleBase,
            Action<string> appendLog)
        {
            _isAttached = isAttached ?? throw new ArgumentNullException(nameof(isAttached));
            _getPid = getPid ?? throw new ArgumentNullException(nameof(getPid));
            _driverRead = driverRead;
            _driverWrite = driverWrite ?? throw new ArgumentNullException(nameof(driverWrite));
            _getPointerSize = getPointerSize ?? (() => IntPtr.Size);
            _getModuleBase = getModuleBase ?? (_ => 0);
            _appendLog = appendLog ?? (_ => { });

            ToggleLockCommand = new RelayCommand(_ => ToggleLock(), _ => _isAttached());
        }

        internal ulong ResolveModule(string name) => _getModuleBase(name);


        // --- Bindable state for the row ---
        private string _addressText = "";
        private string _nameText = "";
        private string _valueText = "";
        private string _currentValueText = "—";
        private string _resolvedAddressText = ""; // NEW
        private LockValueType _valueType = LockValueType.Byte;
        private LockPriority _priority = LockPriority.High;
        private LockGroup _lockGroup = LockGroup.Camera;
        private bool _isLocked;
        private string _lockButtonText = "Lock";
        private readonly object _expressionLock = new();
        private IAddressExpression? _cachedExpression;
        private string _cachedExpressionText = string.Empty;

        // Used by the parent poller to track when this entry was last scanned
        internal long LastPollTick;

        public string AddressText
        {
            get => _addressText;
            set => SetProperty(ref _addressText, value);
        }

        public string ResolvedAddressText // NEW
        {
            get => _resolvedAddressText;
            private set => SetProperty(ref _resolvedAddressText, value);
        }

        public string NameText
        {
            get => _nameText;
            set => SetProperty(ref _nameText, value);
        }

        public string ValueText
        {
            get => _valueText;
            set => SetProperty(ref _valueText, value);
        }

        public string CurrentValueText
        {
            get => _currentValueText;
            private set => SetProperty(ref _currentValueText, value);
        }

        public LockValueType ValueType
        {
            get => _valueType;
            set
            {
                if (SetProperty(ref _valueType, value))
                {
                    // force refresh text formatting on type change
                    CurrentValueText = "…";
                }
            }
        }

        public LockPriority Priority
        {
            get => _priority;
            set => SetProperty(ref _priority, value);
        }

        public LockGroup LockGroup
        {
            get => _lockGroup;
            set => SetProperty(ref _lockGroup, value);
        }

        public bool IsLocked
        {
            get => _isLocked;
            private set => SetProperty(ref _isLocked, value);
        }

        public string LockButtonText
        {
            get => _lockButtonText;
            private set => SetProperty(ref _lockButtonText, value);
        }

        public int ElementSize => ValueType switch
        {
            LockValueType.Byte => 1,
            LockValueType.Short => 2,
            LockValueType.CLong => 4,
            LockValueType.Long => 8,
            _ => 1
        };

        // Commands
        public ICommand ToggleLockCommand { get; }

        // Lock loop internals
        private CancellationTokenSource _cts;

        public void ForceUnlock(string reason = null)
        {
            if (!_isLocked) return;
            _cts?.Cancel();
            if (!string.IsNullOrEmpty(reason))
                AppendLog(reason);
        }

        public void SetResolvedAddress(ulong? addr)
        {
            string txt = addr.HasValue ? $"0x{addr.Value:X}" : "";
            if (txt != _resolvedAddressText)
                SetOnUiThread(() => ResolvedAddressText = txt);
        }

        public bool TryGetAddress(out ulong addr)
        {
            addr = 0;
            if (!TryGetAddressExpression(out var expression))
                return false;

            return expression.TryResolve(this, out addr);
        }

        /// <summary>Called by parent poller after a successful read.</summary>
        public void SetCurrentBytes(byte[] data, int got)
        {
            if (data == null || got < ElementSize)
            {
                SetOnUiThread(() => CurrentValueText = "?");
                return;
            }

            string txt;
            switch (ValueType)
            {
                case LockValueType.Byte:
                    txt = $"{data[0]} (0x{data[0]:X2})";
                    break;
                case LockValueType.Short:
                    {
                        short v = BitConverter.ToInt16(data, 0);
                        txt = $"{v} (0x{(ushort)v:X4})";
                        break;
                    }
                case LockValueType.CLong:
                    {
                        int v = BitConverter.ToInt32(data, 0);
                        txt = $"{v} (0x{(uint)v:X8})";
                        break;
                    }
                case LockValueType.Long:
                    {
                        long v = BitConverter.ToInt64(data, 0);
                        txt = $"{v} (0x{(ulong)v:X16})";
                        break;
                    }
                default:
                    txt = "?";
                    break;
            }

            SetOnUiThread(() => CurrentValueText = txt);
        }

        private void SetOnUiThread(Action act)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) act();
            else d.Invoke(act);
        }

        private async void ToggleLock()
        {
            try
            {
                if (IsLocked)
                {
                    _cts?.Cancel();
                    AppendLog("Unlock requested.");
                    return;
                }

                if (!_isAttached()) { AppendLog("Attach first."); return; }
                if (!TryGetAddressExpression(out var addressExpression))
                {
                    AppendLog("Enter a valid address or pointer expression.");
                    return;
                }

                if (!addressExpression.TryResolve(this, out ulong resolvedAddr))
                {
                    if (addressExpression.UsesPointer && _driverRead == null)
                        AppendLog("Pointer expressions require read support.");
                    else
                        AppendLog("Unable to resolve the address expression.");
                    return;
                }

                string addressForLog = FormatAddressForLog(addressExpression, resolvedAddr);

                byte[] payload;
                string startMsg;

                switch (ValueType)
                {
                    case LockValueType.Byte:
                        if (!TryParseByte(ValueText, out byte b)) { AppendLog("Enter a valid byte value."); return; }
                        payload = new[] { b };
                        startMsg = $"Locking byte 0x{b:X2} @ {addressForLog}";
                        break;

                    case LockValueType.Short:
                        if (!TryParseShort(ValueText, out short s)) { AppendLog("Enter a valid short value."); return; }
                        payload = BitConverter.GetBytes(s); // little-endian on Windows
                        startMsg = $"Locking short {unchecked((ushort)s)} @ {addressForLog}";
                        break;

                    case LockValueType.CLong:
                        if (!TryParseInt(ValueText, out int i)) { AppendLog("Enter a valid C long (32-bit) value."); return; }
                        payload = BitConverter.GetBytes(i); // little-endian
                        startMsg = $"Locking C long {i} @ {addressForLog}";
                        break;

                    case LockValueType.Long:
                        if (!TryParseLong(ValueText, out long l)) { AppendLog("Enter a valid long (64-bit) value."); return; }
                        payload = BitConverter.GetBytes(l); // little-endian
                        startMsg = $"Locking long {l} @ {addressForLog}";
                        break;

                    default:
                        AppendLog("Unsupported value type.");
                        return;
                }

                StartLockLoop(addressExpression, payload, startMsg);
            }
            catch (Exception ex)
            {
                AppendLog("Lock error: " + ex.Message);
            }
        }

        private void StartLockLoop(IAddressExpression addressExpression, byte[] payload, string startMsg)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsLocked = true;
            LockButtonText = "Unlock";
            AppendLog(startMsg);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (!addressExpression.TryResolve(this, out ulong addr))
                        {
                            var failureMessage = addressExpression.UsesPointer && _driverRead == null
                                ? "Pointer expressions require read support; stopping lock."
                                : "Address resolution failed; stopping lock.";
                            AppendLog(failureMessage);
                            break;
                        }

                        var pid = _getPid();

                        bool writeOk = false;
                        uint wrote = 0;
                        var writeTask = Task.Run(() => { writeOk = _driverWrite(pid, addr, payload, out wrote); });

                        if (!writeTask.Wait(500) || !writeOk || wrote != payload.Length)
                        {
                            AppendLog($"WRITE failed or timed out @ 0x{addr:X}; wrote={wrote}.");
                            break;
                        }
                        int lockIntervalMs = LockPriorityConfig.GetLockInterval(_priority);
                        await Task.Delay(lockIntervalMs, ct);
                    }
                }
                catch (TaskCanceledException) { /* normal */ }
                catch (Exception ex) { AppendLog("Lock loop exception: " + ex.Message); }
                finally
                {
                    SetOnUiThread(() =>
                    {
                        IsLocked = false;
                        LockButtonText = "Lock";
                    });
                    if (!ct.IsCancellationRequested)
                        AppendLog("Lock stopped.");
                }
            });
        }

        private bool TryGetAddressExpression(out IAddressExpression expression)
        {
            var text = (AddressText ?? string.Empty).Trim();
            lock (_expressionLock)
            {
                if (_cachedExpression != null && string.Equals(_cachedExpressionText, text, StringComparison.Ordinal))
                {
                    expression = _cachedExpression;
                    return true;
                }

                if (!AddressExpressionParser.TryParse(text, out var parsed))
                {
                    _cachedExpression = null;
                    _cachedExpressionText = text;
                    expression = default!;
                    return false;
                }

                _cachedExpression = parsed;
                _cachedExpressionText = text;
                expression = parsed;
                return true;
            }
        }

        internal bool TryReadPointer(ulong address, out ulong value)
        {
            value = 0;
            var reader = _driverRead;
            if (reader == null)
                return false;

            int pointerSize = _getPointerSize();
            if (pointerSize != 4 && pointerSize != 8)
                pointerSize = IntPtr.Size;
            if (pointerSize != 4 && pointerSize != 8)
                pointerSize = 8;

            var buffer = new byte[pointerSize];
            var pid = _getPid();

            bool readOk = false;
            uint readGot = 0;
            var readTask = Task.Run(() => { readOk = reader(pid, address, buffer, out readGot); });

            if (readTask.Wait(500) && readOk && readGot >= pointerSize)
            {
                value = pointerSize == 4
                    ? BitConverter.ToUInt32(buffer, 0)
                    : BitConverter.ToUInt64(buffer, 0);
                return true;
            }

            return false;
        }

        private string FormatAddressForLog(IAddressExpression expression, ulong resolvedAddr)
        {
            if (!expression.UsesPointer)
                return $"0x{resolvedAddr:X}";

            var raw = (AddressText ?? string.Empty).Trim();
            return string.IsNullOrEmpty(raw)
                ? $"0x{resolvedAddr:X}"
                : $"{raw} (-> 0x{resolvedAddr:X})";
        }


        private static bool TryParseByte(string s, out byte b)
        {
            b = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return byte.TryParse(s[2..], NumberStyles.HexNumber, null, out b);
            if (int.TryParse(s, out int iv) && iv is >= 0 and <= 255) { b = (byte)iv; return true; }
            return false;
        }

        private static bool TryParseShort(string s, out short sh)
        {
            sh = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ushort.TryParse(s[2..], NumberStyles.HexNumber, null, out ushort u))
                {
                    sh = unchecked((short)u);
                    return true;
                }
                return false;
            }
            return short.TryParse(s, out sh);
        }

        private static bool TryParseInt(string s, out int i)
        {
            i = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(s[2..], NumberStyles.HexNumber, null, out uint u))
                {
                    i = unchecked((int)u);
                    return true;
                }
                return false;
            }
            return int.TryParse(s, out i);
        }

        private static bool TryParseLong(string s, out long l)
        {
            l = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ulong.TryParse(s[2..], NumberStyles.HexNumber, null, out var u))
                {
                    l = unchecked((long)u);
                    return true;
                }
                return false;
            }
            return long.TryParse(s, out l);
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            _appendLog($"[{stamp}] {line}");
        }
    }
}
