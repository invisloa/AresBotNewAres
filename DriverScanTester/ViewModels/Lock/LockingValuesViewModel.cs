using DriverScanTester.Models;
using DriverScanTester.Utils;
using DriverScanTester.PointerScan;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Clipboard = System.Windows.Clipboard;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DriverScanTester.ViewModels
{
    public sealed partial class LockingValuesViewModel : BaseViewModel
    {
        // ---------- External plumbing ----------
        public delegate bool DriverReadDelegate(uint pid, ulong remoteAddr, byte[] dst, out uint got);
        public delegate bool DriverWriteDelegate(uint pid, ulong remoteAddr, byte[] src, out uint wrote);

        private readonly DriverReadDelegate? _driverRead;
        private readonly DriverWriteDelegate _driverWrite;
        private readonly Action<string> _appendLog;
        private readonly Func<int> _getPointerSize;
        private readonly Func<string, ulong> _getModuleBase;

        // polling (if you have the live-current feature; harmless otherwise)
        private CancellationTokenSource _pollCts;
        private const int PollBaseIntervalMs = 50;

        // Ticks timestamps for per-entry scan-interval tracking
        private long _pollStartTick;

        public LockingValuesViewModel(
            DriverReadDelegate driverRead,
            DriverWriteDelegate driverWrite,
            Action<string> appendLog,
            Func<int> getPointerSize = null,
            Func<string, ulong> getModuleBase = null)
        {
            _driverRead = driverRead;
            _driverWrite = driverWrite ?? throw new ArgumentNullException(nameof(driverWrite));
            _appendLog = appendLog ?? (_ => { });
            _getPointerSize = getPointerSize ?? (() => IntPtr.Size);
            _getModuleBase = getModuleBase ?? (_ => 0);

            AddEntryCommand = new RelayCommand(_ => AddEntry());
            RemoveEntryCommand = new RelayCommand(entry => { if (entry is LockAddressEntryViewModel vm) RemoveEntry(vm); });
            LockAllCommand = new RelayCommand(_ => LockAll(), _ => IsAttached && Entries.Count > 0);
            UnlockAllCommand = new RelayCommand(_ => UnlockAll(), _ => Entries.Any(e => e.IsLocked));

            SaveEntriesCommand = new RelayCommand(_ => SaveEntries());
            LoadEntriesCommand = new RelayCommand(_ => LoadEntries());

            // NEW: bulk add
            AddBulkFromTextCommand = new RelayCommand(_ => AddBulkFromText());
            PasteAndAddBulkCommand = new RelayCommand(_ => PasteAndAddBulk());

            // Group toggle commands
            ToggleCameraGroupCommand = new RelayCommand(_ => ToggleGroup(LockGroup.Camera));
            ToggleAttackGroupCommand = new RelayCommand(_ => ToggleGroup(LockGroup.Attack));
            ToggleSkillGroupCommand = new RelayCommand(_ => ToggleGroup(LockGroup.Skill));
            ToggleRandomGroupCommand = new RelayCommand(_ => ToggleGroup(LockGroup.Random));

            // Subscribe to collection changes so we can subscribe/unsubscribe per-entry PropertyChanged
            Entries.CollectionChanged += OnEntriesCollectionChanged;
        }

        // ---------- Attachment state ----------
        private bool _isAttached;
        public bool IsAttached
        {
            get => _isAttached;
            set
            {
                if (SetProperty(ref _isAttached, value))
                {
                    if (!value)
                    {
                        foreach (var e in Entries) e.ForceUnlock("Detached; stopping lock.");
                        StopPolling();
                    }
                    else StartPolling();

                    RaiseCanExecutes();
                }
            }
        }

        private uint _attachedPid;
        public uint AttachedPid
        {
            get => _attachedPid;
            set => SetProperty(ref _attachedPid, value);
        }

        // ---------- Group lock state ----------
        private bool _isCameraGroupLocked;
        public bool IsCameraGroupLocked
        {
            get => _isCameraGroupLocked;
            set => SetProperty(ref _isCameraGroupLocked, value);
        }

        private bool _isAttackGroupLocked;
        public bool IsAttackGroupLocked
        {
            get => _isAttackGroupLocked;
            set => SetProperty(ref _isAttackGroupLocked, value);
        }

        private bool _isSkillGroupLocked;
        public bool IsSkillGroupLocked
        {
            get => _isSkillGroupLocked;
            set => SetProperty(ref _isSkillGroupLocked, value);
        }

        private bool _isRandomGroupLocked;
        public bool IsRandomGroupLocked
        {
            get => _isRandomGroupLocked;
            set => SetProperty(ref _isRandomGroupLocked, value);
        }

        // ---------- The list ----------
        public ObservableCollection<LockAddressEntryViewModel> Entries { get; } = new();

        // ---------- Bulk add backing ----------
        private string _bulkInputText = "";
        public string BulkInputText
        {
            get => _bulkInputText;
            set => SetProperty(ref _bulkInputText, value);
        }

        // ---------- Commands ----------
        public ICommand AddEntryCommand { get; }
        public ICommand RemoveEntryCommand { get; }
        public ICommand LockAllCommand { get; }
        public ICommand UnlockAllCommand { get; }

        public ICommand SaveEntriesCommand { get; }
        public ICommand LoadEntriesCommand { get; }

        // NEW
        public ICommand AddBulkFromTextCommand { get; }
        public ICommand PasteAndAddBulkCommand { get; }

        // Group toggle commands
        public ICommand ToggleCameraGroupCommand { get; }
        public ICommand ToggleAttackGroupCommand { get; }
        public ICommand ToggleSkillGroupCommand { get; }
        public ICommand ToggleRandomGroupCommand { get; }

        // Helpers to provide to child entries
        private bool GetIsAttached() => IsAttached;
        private uint GetPid() => AttachedPid;

        private void AddEntry()
        {
            var vm = CreateEntryViewModel();
            vm.AddressText = "";
            vm.ValueText = "";
            Entries.Add(vm);
        }

        private LockAddressEntryViewModel CreateEntryViewModel()
        {
            return new LockAddressEntryViewModel(GetIsAttached, GetPid, _driverRead, _driverWrite, _getPointerSize, _getModuleBase, _appendLog)
            {
                ValueType = LockValueType.Byte,
                LockGroup = LockGroup.Camera
            };
        }

        private void RemoveEntry(LockAddressEntryViewModel vm)
        {
            if (vm == null) return;
            vm.ForceUnlock("Entry removed; stopping lock.");
            vm.PropertyChanged -= OnEntryPropertyChanged;
            Entries.Remove(vm);
            RecalculateGroupStates();
            RaiseCanExecutes();
        }

        private void LockAll()
        {
            foreach (var e in Entries)
                if (!e.IsLocked && e.ToggleLockCommand.CanExecute(null))
                    e.ToggleLockCommand.Execute(null);
            RecalculateGroupStates();
        }

        private void UnlockAll()
        {
            foreach (var e in Entries)
                e.ForceUnlock("Unlock all.");
            RecalculateGroupStates();
        }

        // ---------- Group toggle logic ----------
        private void ToggleGroup(LockGroup group)
        {
            var groupEntries = Entries.Where(e => e.LockGroup == group).ToList();
            if (groupEntries.Count == 0) return;

            bool allLocked = groupEntries.All(e => e.IsLocked);

            if (allLocked)
            {
                foreach (var e in groupEntries)
                    e.ForceUnlock($"Group {group} unlock.");
            }
            else
            {
                foreach (var e in groupEntries)
                    if (!e.IsLocked && e.ToggleLockCommand.CanExecute(null))
                        e.ToggleLockCommand.Execute(null);
            }

            RecalculateGroupStates();
        }

        private void RecalculateGroupStates()
        {
            var camera = Entries.Where(e => e.LockGroup == LockGroup.Camera).ToList();
            IsCameraGroupLocked = camera.Count > 0 && camera.All(e => e.IsLocked);

            var attack = Entries.Where(e => e.LockGroup == LockGroup.Attack).ToList();
            IsAttackGroupLocked = attack.Count > 0 && attack.All(e => e.IsLocked);

            var skill = Entries.Where(e => e.LockGroup == LockGroup.Skill).ToList();
            IsSkillGroupLocked = skill.Count > 0 && skill.All(e => e.IsLocked);

            var random = Entries.Where(e => e.LockGroup == LockGroup.Random).ToList();
            IsRandomGroupLocked = random.Count > 0 && random.All(e => e.IsLocked);
        }

        private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LockAddressEntryViewModel.IsLocked))
            {
                RecalculateGroupStates();
            }
        }

        private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (LockAddressEntryViewModel vm in e.OldItems)
                {
                    vm.PropertyChanged -= OnEntryPropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (LockAddressEntryViewModel vm in e.NewItems)
                {
                    vm.PropertyChanged += OnEntryPropertyChanged;
                }
            }

            RecalculateGroupStates();
            RaiseCanExecutes();
        }

        // ---------- BULK ADD ----------
        private void AddBulkFromText()
        {
            var text = BulkInputText ?? "";
            int added = AddAddressesFromString(text);
            _appendLog?.Invoke($"Added {added} address{(added == 1 ? "" : "es")} from text.");
        }

        private void PasteAndAddBulk()
        {
            try
            {
                var clipboard = Clipboard.GetText() ?? "";
                int added = AddAddressesFromString(clipboard);
                _appendLog?.Invoke($"Added {added} address{(added == 1 ? "" : "es")} from clipboard.");
            }
            catch (Exception ex)
            {
                _appendLog?.Invoke($"Clipboard error: {ex.Message}");
            }
        }

        private int AddAddressesFromString(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 0;

            int addedCount = 0;
            string trimmed = input.Trim();

            // Existing addresses for dedup (normalized as 0x... uppercase)
            var existing = Entries
                .Select(e => NormalizeAddrString(e.AddressText))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 1. Try to parse as JSON (either a single object or an array of PersistedPointerChain)
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
                    var chains = new List<PointerScanner.PersistedPointerChain>();

                    if (trimmed.StartsWith("["))
                    {
                        var list = JsonSerializer.Deserialize<List<PointerScanner.PersistedPointerChain>>(trimmed, options);
                        if (list != null) chains.AddRange(list);
                    }
                    else
                    {
                        var single = JsonSerializer.Deserialize<PointerScanner.PersistedPointerChain>(trimmed, options);
                        if (single != null) chains.Add(single);
                    }

                    foreach (var chain in chains)
                    {
                        string expr = ConvertChainToExpression(chain.ModuleName, chain.Offsets);
                        if (string.IsNullOrEmpty(expr)) continue;

                        // Check dedup based on expression string (or normalized address if it were a constant)
                        if (Entries.Any(e => string.Equals(e.AddressText, expr, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var vm = CreateEntryViewModel();
                        vm.AddressText = expr;
                        vm.NameText = chain.ModuleName + " Chain";
                        vm.ValueType = LockValueType.CLong; // Default to 4-byte for common game values
                        vm.ValueText = "";
                        Entries.Add(vm);
                        addedCount++;
                    }

                    if (addedCount > 0) return addedCount;
                }
                catch
                {
                    // Fall through to line-by-line parsing if JSON parsing fails
                }
            }

            // 2. Line-by-line: each line is either a pointer expression or one/more hex/dec addresses
            foreach (var rawLine in input.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim().Trim('"', '\'', '`');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // 2a. If line contains brackets, try parsing as a pointer expression
                if (line.Contains('[') || line.Contains(']'))
                {
                    if (AddressExpressionParser.TryParse(line, out _))
                    {
                        if (Entries.Any(e => string.Equals(e.AddressText.Trim(), line, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var vm = CreateEntryViewModel();
                        vm.AddressText = line;
                        vm.NameText = line;
                        vm.ValueType = LockValueType.CLong;
                        vm.ValueText = "";
                        Entries.Add(vm);
                        addedCount++;
                        continue;
                    }
                }

                // 2b. Fall back to token splitting for simple hex/dec addresses on this line
                foreach (var token in SplitAddressTokens(line))
                {
                    if (!TryParseAddrToken(token, out ulong addr))
                        continue;

                    string normalized = $"0x{addr:X}";
                    if (existing.Contains(normalized))
                        continue;

                    var vm = CreateEntryViewModel();
                    vm.AddressText = normalized;
                    vm.NameText = normalized;
                    vm.ValueType = LockValueType.Byte;
                    vm.ValueText = "";
                    Entries.Add(vm);
                    existing.Add(normalized);
                    addedCount++;
                }
            }
            return addedCount;
        }

        private static string ConvertChainToExpression(string moduleName, long[] offsets)
        {
            if (string.IsNullOrEmpty(moduleName) || offsets == null || offsets.Length == 0) return string.Empty;

            if (offsets.Length == 1)
            {
                return $"{moduleName} {FormatOffset(offsets[0])}";
            }

            string expr = $"[{moduleName} {FormatOffset(offsets[0])}]";
            for (int i = 1; i < offsets.Length - 1; i++)
            {
                expr = $"[{expr} {FormatOffset(offsets[i])}]";
            }
            expr += $" {FormatOffset(offsets[^1])}";
            return expr;
        }

        private static string FormatOffset(long offset)
        {
            if (offset >= 0)
                return $"+ 0x{offset:X}";

            return $"- 0x{Math.Abs(offset):X}";
        }

        private static string NormalizeAddrString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ulong.TryParse(s[2..], NumberStyles.HexNumber, null, out var v))
                    return $"0x{v:X}";
                return null;
            }
            // decimal
            if (ulong.TryParse(s, NumberStyles.Integer, null, out var d))
                return $"0x{d:X}";
            // bare hex without 0x (optional)
            if (ulong.TryParse(s, NumberStyles.HexNumber, null, out var h))
                return $"0x{h:X}";
            return null;
        }

        private static IEnumerable<string> SplitAddressTokens(string text)
        {
            // Accept lines or comma/space-separated tokens, strip quotes/backticks
            return text
                .Replace("\r", "")
                .Split(new[] { '\n', ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().Trim('"', '\'', '`'));
        }

        private static bool TryParseAddrToken(string token, out ulong addr)
        {
            addr = 0;
            if (string.IsNullOrWhiteSpace(token)) return false;

            var t = token.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(t[2..], NumberStyles.HexNumber, null, out addr);

            // decimal first…
            if (ulong.TryParse(t, NumberStyles.Integer, null, out addr)) return true;

            // …or bare HEX (e.g., 16F31780)
            return ulong.TryParse(t, NumberStyles.HexNumber, null, out addr);
        }

        // ---------- Save / Load ----------
        private void SaveEntries()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    FileName = "lock_entries.json"
                };

                if (dialog.ShowDialog() == true)
                {
                    var dtoList = Entries.Select(e => new EntryDto
                    {
                        AddressText = e.AddressText,
                        ValueText = e.ValueText,
                        ValueType = e.ValueType,
                        NameText = e.NameText,
                        Priority = e.Priority,
                        LockGroup = e.LockGroup
                    }).ToList();

                    var json = JsonSerializer.Serialize(dtoList, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(dialog.FileName, json);

                    _appendLog?.Invoke($"Saved {dtoList.Count} entr{(dtoList.Count == 1 ? "y" : "ies")} to '{dialog.FileName}'.");
                }
            }
            catch (Exception ex)
            {
                _appendLog?.Invoke($"Save error: {ex.Message}");
            }
        }

        private void LoadEntries()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
                };

                if (dialog.ShowDialog() == true)
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var dtoList = JsonSerializer.Deserialize<EntryDto[]>(
                        json,
                        new JsonSerializerOptions { AllowTrailingCommas = true });

                    if (dtoList == null)
                    {
                        _appendLog?.Invoke("Load error: File did not contain any entries.");
                        return;
                    }

                    // Get set of existing normalized addresses to avoid duplicates
                    var existing = Entries
                        .Select(e => NormalizeAddrString(e.AddressText))
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    int added = 0;
                    foreach (var dto in dtoList)
                    {
                        string normalized = NormalizeAddrString(dto.AddressText);
                        if (normalized != null && existing.Contains(normalized))
                            continue;

                        var vm = CreateEntryViewModel();
                        vm.AddressText = dto.AddressText ?? string.Empty;
                        vm.NameText = dto.NameText ?? (dto.AddressText ?? "");
                        vm.ValueText = dto.ValueText ?? string.Empty;
                        vm.ValueType = dto.ValueType;
                        vm.Priority = dto.Priority;
                        vm.LockGroup = dto.LockGroup;
                        Entries.Add(vm);
                        if (normalized != null) existing.Add(normalized);
                        added++;
                    }

                    _appendLog?.Invoke($"Appended {added} entr{(added == 1 ? "" : "ies")} from '{dialog.FileName}'.");
                    RaiseCanExecutes();
                    if (IsAttached) StartPolling();
                }
            }
            catch (Exception ex)
            {
                _appendLog?.Invoke($"Load error: {ex.Message}");
            }
        }

        private void RaiseCanExecutes()
        {
            if (AddEntryCommand is RelayCommand r1) r1.RaiseCanExecuteChanged();
            if (RemoveEntryCommand is RelayCommand r2) r2.RaiseCanExecuteChanged();
            if (LockAllCommand is RelayCommand r3) r3.RaiseCanExecuteChanged();
            if (UnlockAllCommand is RelayCommand r4) r4.RaiseCanExecuteChanged();
            if (SaveEntriesCommand is RelayCommand r5) r5.RaiseCanExecuteChanged();
            if (LoadEntriesCommand is RelayCommand r6) r6.RaiseCanExecuteChanged();
            if (AddBulkFromTextCommand is RelayCommand r7) r7.RaiseCanExecuteChanged();
            if (PasteAndAddBulkCommand is RelayCommand r8) r8.RaiseCanExecuteChanged();
            if (ToggleCameraGroupCommand is RelayCommand r9) r9.RaiseCanExecuteChanged();
            if (ToggleAttackGroupCommand is RelayCommand r10) r10.RaiseCanExecuteChanged();
            if (ToggleSkillGroupCommand is RelayCommand r11) r11.RaiseCanExecuteChanged();
            if (ToggleRandomGroupCommand is RelayCommand r12) r12.RaiseCanExecuteChanged();
        }

        // ---------- live polling with per-entry priority scan intervals ----------
        private void StartPolling()
        {
            if (_driverRead == null) return;
            StopPolling();
            _pollCts = new CancellationTokenSource();
            var ct = _pollCts.Token;
            _pollStartTick = Environment.TickCount64;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && IsAttached)
                    {
                        var pid = AttachedPid;
                        var now = Environment.TickCount64;
                        var list = Entries.ToList();

                        foreach (var entry in list)
                        {
                            if (ct.IsCancellationRequested) break;

                            int scanIntervalMs = LockPriorityConfig.GetScanInterval(entry.Priority);
                            if (now - entry.LastPollTick < scanIntervalMs)
                                continue;

                            entry.LastPollTick = now;

                            var currentEntry = entry;
                            var currentPid = pid;

                            var entryTask = Task.Run(() =>
                            {
                                if (!currentEntry.TryGetAddress(out ulong addr) || addr == 0)
                                {
                                    currentEntry.SetResolvedAddress(null);
                                    currentEntry.SetCurrentBytes(null, 0);
                                    return;
                                }

                                currentEntry.SetResolvedAddress(addr);

                                int size = currentEntry.ElementSize;
                                var buf = new byte[size];
                                if (_driverRead(currentPid, addr, buf, out uint got) && got >= size)
                                    currentEntry.SetCurrentBytes(buf, (int)got);
                                else
                                    currentEntry.SetCurrentBytes(null, 0);
                            });

                            if (!entryTask.Wait(500))
                            {
                                _appendLog?.Invoke($"Poll timeout for '{currentEntry.NameText}'");
                            }
                        }

                        await Task.Delay(PollBaseIntervalMs, ct);
                    }
                }
                catch (TaskCanceledException) { }
                catch (Exception ex) { _appendLog?.Invoke($"Poll loop error: {ex.Message}"); }
            });
        }

        private void StopPolling()
        {
            try { _pollCts?.Cancel(); } catch { }
            _pollCts = null;
        }

        // ---------- DTO ----------
        private sealed class EntryDto
        {
            public string AddressText { get; set; }
            public string NameText { get; set; }
            public string ValueText { get; set; }
            public LockValueType ValueType { get; set; }
            public LockPriority Priority { get; set; } = LockPriority.High;
            public LockGroup LockGroup { get; set; } = LockGroup.Camera;
        }
    }
}
