using DriverScanTester.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;

namespace DriverScanTester.ViewModels
{
    // Delegate matching your MainViewModel.DriverRead
    public delegate bool DriverReadDelegate(uint pid, ulong remoteAddr, byte[] dst, out uint got);

    public sealed class HexViewModel : INotifyPropertyChanged
    {
        private const int BYTES_PER_ROW = 16;

        private readonly DriverReadDelegate _driverRead;
        private readonly Action<string> _log;

        private bool _isAttached;
        private uint _attachedPid;

        private ulong _centerAddress;
        private int _rangeBytes = 200;
        private bool _autoRefresh;
        private int _refreshMs = 500;
        private readonly DispatcherTimer _timer;

        private string _addressText = "0x0";
        private string _statusText = "";
        private HexRow? _selectedRow;
        private int _selectedByteOffset = -1;

        // Keep previous snapshot per row address for highlighting
        private readonly Dictionary<ulong, byte?[]> _lastRowBytes = new();
        // Permanent change tracking across refreshes: row address -> per-byte flags
        private readonly Dictionary<ulong, bool[]> _everChangedState = new();

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<HexRow> Rows { get; } = new();

        public HexRow? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (Set(ref _selectedRow, value))
                {
                    if (value == null) _selectedByteOffset = -1;
                    OnPropertyChanged(nameof(SelectedAddressHex));
                    OnPropertyChanged(nameof(SelectedRowHex));
                    OnPropertyChanged(nameof(SelectedByteValue));
                    OnPropertyChanged(nameof(CanCopy));
                }
            }
        }

        public int SelectedByteOffset
        {
            get => _selectedByteOffset;
            set
            {
                if (Set(ref _selectedByteOffset, value))
                {
                    OnPropertyChanged(nameof(SelectedAddressHex));
                    OnPropertyChanged(nameof(SelectedByteValue));
                }
            }
        }

        public string SelectedAddressHex
        {
            get
            {
                if (SelectedRow == null) return "";
                if (_selectedByteOffset >= 0 && _selectedByteOffset < 16)
                    return $"0x{SelectedRow.Address + (uint)_selectedByteOffset:X16}";
                return SelectedRow.AddressHex;
            }
        }

        public string SelectedRowHex => SelectedRow != null ? FormatRowHex(SelectedRow) : "";
        public string SelectedByteValue => SelectedRow != null && _selectedByteOffset >= 0 && _selectedByteOffset < 16 ? GetByteDisplay(SelectedRow, _selectedByteOffset) : "";
        public bool CanCopy => SelectedRow != null;

        public bool IsAttached
        {
            get => _isAttached;
            set { if (Set(ref _isAttached, value)) OnPropertyChanged(nameof(CanRead)); }
        }

        public uint AttachedPid
        {
            get => _attachedPid;
            set => Set(ref _attachedPid, value);
        }

        public string AddressText
        {
            get => _addressText;
            set => Set(ref _addressText, value);
        }

        public int RangeBytes
        {
            get => _rangeBytes;
            set
            {
                if (Set(ref _rangeBytes, value)) Refresh();
            }
        }

        public bool AutoRefresh
        {
            get => _autoRefresh;
            set
            {
                if (Set(ref _autoRefresh, value))
                {
                    if (value) StartTimer();
                    else _timer.Stop();
                }
            }
        }

        public int RefreshMs
        {
            get => _refreshMs;
            set
            {
                if (value < 100) value = 100;
                if (value > 10000) value = 10000;
                if (Set(ref _refreshMs, value))
                {
                    _timer.Interval = TimeSpan.FromMilliseconds(_refreshMs);
                    if (AutoRefresh) StartTimer();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        public bool CanRead => IsAttached && AttachedPid != 0;

        public ICommand GoCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand CenterMinus0x10Command { get; }
        public ICommand CenterPlus0x10Command { get; }
        public ICommand CopyAddressCommand { get; }
        public ICommand CopyRowCommand { get; }
        public ICommand ClearChangedCommand { get; }

        public HexViewModel(DriverReadDelegate driverRead, Action<string> log)
        {
            _driverRead = driverRead ?? throw new ArgumentNullException(nameof(driverRead));
            _log = log ?? (_ => { });

            GoCommand = new RelayCommand(_ => GoToAddressFromText(), _ => CanRead);
            RefreshCommand = new RelayCommand(_ => Refresh(), _ => CanRead);
            CenterMinus0x10Command = new RelayCommand(_ => NudgeCenter(-0x10), _ => CanRead);
            CenterPlus0x10Command = new RelayCommand(_ => NudgeCenter(+0x10), _ => CanRead);
            CopyAddressCommand = new RelayCommand(_ => CopySelectedAddress(), _ => CanCopy);
            CopyRowCommand = new RelayCommand(_ => CopySelectedRow(), _ => CanCopy);
            ClearChangedCommand = new RelayCommand(_ => ClearChanged(), _ => CanRead);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_refreshMs) };
            _timer.Tick += (_, __) => Refresh();
        }

        public void InitializeAt(ulong address, bool autoRefreshStart = true)
        {
            _centerAddress = address;
            AddressText = $"0x{address:X}";
            Refresh();
            AutoRefresh = autoRefreshStart;
        }

        private void StartTimer()
        {
            _timer.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(_refreshMs);
            _timer.Start();
        }

        private void NudgeCenter(int delta)
        {
            unchecked { _centerAddress = (ulong)((long)_centerAddress + delta); }
            AddressText = $"0x{_centerAddress:X}";
            Refresh();
        }

        private static bool TryParseAddress(string s, out ulong addr)
        {
            addr = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out addr);
            return ulong.TryParse(s, out addr);
        }

        private void GoToAddressFromText()
        {
            if (!CanRead) { StatusText = "Attach first."; return; }
            if (!TryParseAddress(AddressText, out var a)) { StatusText = "Invalid address."; return; }
            _centerAddress = a;
            Refresh();
        }

        private static string FormatRowHex(HexRow row)
        {
            var sb = new StringBuilder(48);
            for (int i = 0; i < 16; i++)
            {
                if (i > 0 && i % 8 == 0) sb.Append(' ');
                string val = GetByteDisplay(row, i);
                sb.Append(val);
            }
            return sb.ToString();
        }

        private static string GetByteDisplay(HexRow row, int idx)
        {
            switch (idx)
            {
                case 0: return row.B00;
                case 1: return row.B01;
                case 2: return row.B02;
                case 3: return row.B03;
                case 4: return row.B04;
                case 5: return row.B05;
                case 6: return row.B06;
                case 7: return row.B07;
                case 8: return row.B08;
                case 9: return row.B09;
                case 10: return row.B0A;
                case 11: return row.B0B;
                case 12: return row.B0C;
                case 13: return row.B0D;
                case 14: return row.B0E;
                case 15: return row.B0F;
                default: return "..";
            }
        }

        public void SetSelectedCell(HexRow row, int byteOffset)
        {
            _selectedRow = row;
            _selectedByteOffset = byteOffset;
            OnPropertyChanged(nameof(SelectedRow));
            OnPropertyChanged(nameof(SelectedByteOffset));
            OnPropertyChanged(nameof(SelectedAddressHex));
            OnPropertyChanged(nameof(SelectedRowHex));
            OnPropertyChanged(nameof(SelectedByteValue));
            OnPropertyChanged(nameof(CanCopy));
        }

        private static bool[] GetRowEverChanged(HexRow row)
        {
            var flags = new bool[16];
            flags[0] = row.B00EverChanged;
            flags[1] = row.B01EverChanged;
            flags[2] = row.B02EverChanged;
            flags[3] = row.B03EverChanged;
            flags[4] = row.B04EverChanged;
            flags[5] = row.B05EverChanged;
            flags[6] = row.B06EverChanged;
            flags[7] = row.B07EverChanged;
            flags[8] = row.B08EverChanged;
            flags[9] = row.B09EverChanged;
            flags[10] = row.B0AEverChanged;
            flags[11] = row.B0BEverChanged;
            flags[12] = row.B0CEverChanged;
            flags[13] = row.B0DEverChanged;
            flags[14] = row.B0EEverChanged;
            flags[15] = row.B0FEverChanged;
            return flags;
        }

        private void CopySelectedAddress()
        {
            if (SelectedRow == null) return;
            try { System.Windows.Clipboard.SetText(SelectedAddressHex); }
            catch { StatusText = "Failed to copy address."; }
        }

        private void CopySelectedRow()
        {
            if (SelectedRow == null) return;
            try
            {
                string text = $"{SelectedRow.AddressHex}  {SelectedRowHex}  {SelectedRow.ASCII}";
                System.Windows.Clipboard.SetText(text);
            }
            catch { StatusText = "Failed to copy row."; }
        }

        public void Refresh()
        {
            if (!CanRead) { StatusText = "Attach first."; return; }

            // Compute window and align to 16 bytes
            int len = RangeBytes;
            if (len < 16) len = 16;
            if (len > 4096) len = 4096;
            if ((len % BYTES_PER_ROW) != 0) len += (BYTES_PER_ROW - (len % BYTES_PER_ROW));
            ulong half = (ulong)(len / 2);
            ulong start = _centerAddress >= half ? _centerAddress - half : 0UL;
            start &= ~0xFUL;

            byte?[] buffer = new byte?[len];
            int filled = 0;
            int unreadable = 0;

            // read in 16-byte chunks; tolerate unreadable lines
            for (int off = 0; off < len; off += BYTES_PER_ROW)
            {
                byte[] tmp = new byte[BYTES_PER_ROW];
                if (_driverRead(AttachedPid, start + (ulong)off, tmp, out uint got) && got > 0)
                {
                    int copy = Math.Min((int)got, BYTES_PER_ROW);
                    for (int i = 0; i < copy; i++) buffer[off + i] = tmp[i];
                    filled += copy;
                    if (got < BYTES_PER_ROW) unreadable += (BYTES_PER_ROW - (int)got);
                }
                else
                {
                    unreadable += BYTES_PER_ROW;
                }
            }

            // Preserve selected row address and ever-changed state across refresh
            ulong? prevSelectedAddr = _selectedRow?.Address;
            foreach (var row in Rows)
                _everChangedState[row.Address] = GetRowEverChanged(row);

            // Build rows with change highlighting using _lastRowBytes
            Rows.Clear();
            HexRow? restoredRow = null;
            for (int i = 0; i < len; i += BYTES_PER_ROW)
            {
                ulong rowAddr = start + (ulong)i;
                var row = new HexRow(rowAddr);

                // get previous row bytes for comparison (if any)
                _lastRowBytes.TryGetValue(rowAddr, out var prev);

                for (int j = 0; j < BYTES_PER_ROW; j++)
                {
                    byte? cur = buffer[i + j];
                    byte? old = prev != null && j < prev.Length ? prev[j] : null;
                    row.SetByte(j, cur, old);
                }

                // restore permanent change flags saved from previous rows
                if (_everChangedState.TryGetValue(rowAddr, out var savedEver))
                    row.RestoreEverChanged(savedEver);

                row.FinalizeAscii();
                Rows.Add(row);

                if (prevSelectedAddr.HasValue && row.Address == prevSelectedAddr.Value)
                    restoredRow = row;

                // store a copy for next time
                var snap = new byte?[BYTES_PER_ROW];
                for (int j = 0; j < BYTES_PER_ROW; j++) snap[j] = buffer[i + j];
                _lastRowBytes[rowAddr] = snap;
            }

            SelectedRow = restoredRow;

            StatusText = $"Showing {len} bytes @ 0x{start:X} … 0x{start + (ulong)len - 1:X} | filled ~{filled} bytes, {unreadable} unreadable.";
        }

        public void ClearChanged()
        {
            if (!CanRead) return;

            int len = RangeBytes;
            if (len < 16) len = 16;
            if (len > 4096) len = 4096;
            if ((len % BYTES_PER_ROW) != 0) len += (BYTES_PER_ROW - (len % BYTES_PER_ROW));
            ulong half = (ulong)(len / 2);
            ulong start = _centerAddress >= half ? _centerAddress - half : 0UL;
            start &= ~0xFUL;

            byte?[] buffer = new byte?[len];
            int filled = 0;
            int unreadable = 0;

            for (int off = 0; off < len; off += BYTES_PER_ROW)
            {
                byte[] tmp = new byte[BYTES_PER_ROW];
                if (_driverRead(AttachedPid, start + (ulong)off, tmp, out uint got) && got > 0)
                {
                    int copy = Math.Min((int)got, BYTES_PER_ROW);
                    for (int i = 0; i < copy; i++) buffer[off + i] = tmp[i];
                    filled += copy;
                    if (got < BYTES_PER_ROW) unreadable += (BYTES_PER_ROW - (int)got);
                }
                else
                {
                    unreadable += BYTES_PER_ROW;
                }
            }

            for (int i = 0; i < len; i += BYTES_PER_ROW)
            {
                ulong rowAddr = start + (ulong)i;
                var snap = new byte?[BYTES_PER_ROW];
                for (int j = 0; j < BYTES_PER_ROW; j++)
                    snap[j] = buffer[i + j];
                _lastRowBytes[rowAddr] = snap;
            }

            _everChangedState.Clear();
            Rows.Clear();

            for (int i = 0; i < len; i += BYTES_PER_ROW)
            {
                ulong rowAddr = start + (ulong)i;
                var row = new HexRow(rowAddr);
                for (int j = 0; j < BYTES_PER_ROW; j++)
                    row.SetByte(j, buffer[i + j], buffer[i + j]);
                row.FinalizeAscii();
                Rows.Add(row);
            }

            SelectedRow = null;

            StatusText = $"Showing {len} bytes @ 0x{start:X} … 0x{start + (ulong)len - 1:X} | filled ~{filled} bytes, {unreadable} unreadable. Highlights cleared.";
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string prop = "")
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            return true;
        }

        private void OnPropertyChanged(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public sealed class HexRow : INotifyPropertyChanged
    {
        private const int N = 16;

        private readonly byte?[] _bytes = new byte?[N];
        private readonly bool[] _changed = new bool[N];
        private readonly bool[] _everChanged = new bool[N];

        private string _ascii = "";

        public event PropertyChangedEventHandler PropertyChanged;

        public ulong Address { get; }
        public string AddressHex => $"0x{Address:X16}";

        // 16 hex cells as strings
        public string B00 { get; private set; } = "..";
        public string B01 { get; private set; } = "..";
        public string B02 { get; private set; } = "..";
        public string B03 { get; private set; } = "..";
        public string B04 { get; private set; } = "..";
        public string B05 { get; private set; } = "..";
        public string B06 { get; private set; } = "..";
        public string B07 { get; private set; } = "..";
        public string B08 { get; private set; } = "..";
        public string B09 { get; private set; } = "..";
        public string B0A { get; private set; } = "..";
        public string B0B { get; private set; } = "..";
        public string B0C { get; private set; } = "..";
        public string B0D { get; private set; } = "..";
        public string B0E { get; private set; } = "..";
        public string B0F { get; private set; } = "..";

        // 16 transient change flags for styling (resets each refresh)
        public bool B00Changed { get; private set; }
        public bool B01Changed { get; private set; }
        public bool B02Changed { get; private set; }
        public bool B03Changed { get; private set; }
        public bool B04Changed { get; private set; }
        public bool B05Changed { get; private set; }
        public bool B06Changed { get; private set; }
        public bool B07Changed { get; private set; }
        public bool B08Changed { get; private set; }
        public bool B09Changed { get; private set; }
        public bool B0AChanged { get; private set; }
        public bool B0BChanged { get; private set; }
        public bool B0CChanged { get; private set; }
        public bool B0DChanged { get; private set; }
        public bool B0EChanged { get; private set; }
        public bool B0FChanged { get; private set; }

        // 16 permanent change flags (stays true once set)
        public bool B00EverChanged { get; private set; }
        public bool B01EverChanged { get; private set; }
        public bool B02EverChanged { get; private set; }
        public bool B03EverChanged { get; private set; }
        public bool B04EverChanged { get; private set; }
        public bool B05EverChanged { get; private set; }
        public bool B06EverChanged { get; private set; }
        public bool B07EverChanged { get; private set; }
        public bool B08EverChanged { get; private set; }
        public bool B09EverChanged { get; private set; }
        public bool B0AEverChanged { get; private set; }
        public bool B0BEverChanged { get; private set; }
        public bool B0CEverChanged { get; private set; }
        public bool B0DEverChanged { get; private set; }
        public bool B0EEverChanged { get; private set; }
        public bool B0FEverChanged { get; private set; }

        public string ASCII
        {
            get => _ascii;
            private set
            {
                if (_ascii != value)
                {
                    _ascii = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ASCII)));
                }
            }
        }

        public HexRow(ulong address) => Address = address;

        public void SetByte(int idx, byte? current, byte? previous)
        {
            _bytes[idx] = current;

            bool changed = HasChanged(previous, current);
            _changed[idx] = changed;

            if (changed && !_everChanged[idx])
            {
                _everChanged[idx] = true;
                SetEverChangedFlag(idx, true);
            }

            string cell = current.HasValue ? current.Value.ToString("X2") : "..";
            SetCellText(idx, cell);
            SetChangedFlag(idx, changed);
        }

        private static bool HasChanged(byte? prev, byte? cur)
        {
            // treat null->value or value->null as changed; equal values unchanged
            if (!prev.HasValue && !cur.HasValue) return false;
            if (prev.HasValue != cur.HasValue) return true;
            return prev.Value != cur.Value;
        }

        private void SetCellText(int idx, string value)
        {
            switch (idx)
            {
                case 0: B00 = value; Raise(nameof(B00)); break;
                case 1: B01 = value; Raise(nameof(B01)); break;
                case 2: B02 = value; Raise(nameof(B02)); break;
                case 3: B03 = value; Raise(nameof(B03)); break;
                case 4: B04 = value; Raise(nameof(B04)); break;
                case 5: B05 = value; Raise(nameof(B05)); break;
                case 6: B06 = value; Raise(nameof(B06)); break;
                case 7: B07 = value; Raise(nameof(B07)); break;
                case 8: B08 = value; Raise(nameof(B08)); break;
                case 9: B09 = value; Raise(nameof(B09)); break;
                case 10: B0A = value; Raise(nameof(B0A)); break;
                case 11: B0B = value; Raise(nameof(B0B)); break;
                case 12: B0C = value; Raise(nameof(B0C)); break;
                case 13: B0D = value; Raise(nameof(B0D)); break;
                case 14: B0E = value; Raise(nameof(B0E)); break;
                case 15: B0F = value; Raise(nameof(B0F)); break;
            }
        }

        private void SetChangedFlag(int idx, bool changed)
        {
            switch (idx)
            {
                case 0: B00Changed = changed; Raise(nameof(B00Changed)); break;
                case 1: B01Changed = changed; Raise(nameof(B01Changed)); break;
                case 2: B02Changed = changed; Raise(nameof(B02Changed)); break;
                case 3: B03Changed = changed; Raise(nameof(B03Changed)); break;
                case 4: B04Changed = changed; Raise(nameof(B04Changed)); break;
                case 5: B05Changed = changed; Raise(nameof(B05Changed)); break;
                case 6: B06Changed = changed; Raise(nameof(B06Changed)); break;
                case 7: B07Changed = changed; Raise(nameof(B07Changed)); break;
                case 8: B08Changed = changed; Raise(nameof(B08Changed)); break;
                case 9: B09Changed = changed; Raise(nameof(B09Changed)); break;
                case 10: B0AChanged = changed; Raise(nameof(B0AChanged)); break;
                case 11: B0BChanged = changed; Raise(nameof(B0BChanged)); break;
                case 12: B0CChanged = changed; Raise(nameof(B0CChanged)); break;
                case 13: B0DChanged = changed; Raise(nameof(B0DChanged)); break;
                case 14: B0EChanged = changed; Raise(nameof(B0EChanged)); break;
                case 15: B0FChanged = changed; Raise(nameof(B0FChanged)); break;
            }
        }

        private void SetEverChangedFlag(int idx, bool changed)
        {
            switch (idx)
            {
                case 0: B00EverChanged = changed; Raise(nameof(B00EverChanged)); break;
                case 1: B01EverChanged = changed; Raise(nameof(B01EverChanged)); break;
                case 2: B02EverChanged = changed; Raise(nameof(B02EverChanged)); break;
                case 3: B03EverChanged = changed; Raise(nameof(B03EverChanged)); break;
                case 4: B04EverChanged = changed; Raise(nameof(B04EverChanged)); break;
                case 5: B05EverChanged = changed; Raise(nameof(B05EverChanged)); break;
                case 6: B06EverChanged = changed; Raise(nameof(B06EverChanged)); break;
                case 7: B07EverChanged = changed; Raise(nameof(B07EverChanged)); break;
                case 8: B08EverChanged = changed; Raise(nameof(B08EverChanged)); break;
                case 9: B09EverChanged = changed; Raise(nameof(B09EverChanged)); break;
                case 10: B0AEverChanged = changed; Raise(nameof(B0AEverChanged)); break;
                case 11: B0BEverChanged = changed; Raise(nameof(B0BEverChanged)); break;
                case 12: B0CEverChanged = changed; Raise(nameof(B0CEverChanged)); break;
                case 13: B0DEverChanged = changed; Raise(nameof(B0DEverChanged)); break;
                case 14: B0EEverChanged = changed; Raise(nameof(B0EEverChanged)); break;
                case 15: B0FEverChanged = changed; Raise(nameof(B0FEverChanged)); break;
            }
        }

        public void RestoreEverChanged(bool[] flags)
        {
            for (int i = 0; i < N && i < flags.Length; i++)
            {
                if (flags[i] && !_everChanged[i])
                {
                    _everChanged[i] = true;
                    SetEverChangedFlag(i, true);
                }
            }
        }

        public void FinalizeAscii()
        {
            var sb = new StringBuilder(N);
            for (int i = 0; i < N; i++)
            {
                var b = _bytes[i];
                if (!b.HasValue) { sb.Append('·'); continue; }
                char c = (char)b.Value;
                if (c >= 32 && c < 127) sb.Append(c);
                else sb.Append('.');
            }
            ASCII = sb.ToString();
        }

        private void Raise(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
