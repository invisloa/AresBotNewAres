using DriverScanTester.PointerScan;
using DriverScanTester.Utils;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace DriverScanTester.ViewModels
{
    public sealed class PointerScanViewModel : BaseViewModel, IDisposable
    {
        private readonly PointerScanner _scanner;
        private readonly PointerScanner.PointerScanOptions _scanOptions = new();
        private readonly PointerScanner.PointerChainOptions _chainOptions = new();
        private CancellationTokenSource _cts;

        private string _targetAddressText = string.Empty;
        private string _targetEndText = string.Empty;
        private string _rangeStartText = string.Empty;
        private string _rangeEndText = string.Empty;
        private string _alignmentText = string.Empty;
        private string _maxOffsetText = "1000";
        private string _maxResultsText = "256";
        private bool _modulesOnly;
        private string _statusMessage = string.Empty;
        private string _chainJson = string.Empty;
        private PointerScanResultViewModel? _selectedResult;
        private PointerScanner.ModuleInfo? _selectedModule;

        public PointerScanViewModel(PointerScanner scanner)
        {
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            Title = "Pointer Scan";
            AlignmentText = "4"; // Default to alignment 1 for thoroughness
            _cts = new CancellationTokenSource();

            SuggestCommand = new RelayCommand(_ => SuggestRange(), _ => CanSuggestRange());
            ScanCommand = new RelayCommand(async _ => await RunScanAsync(), _ => !IsBusy);
            CancelCommand = new RelayCommand(_ => CancelScan(), _ => IsBusy);
            ResolveChainCommand = new RelayCommand(async _ => await ResolveChainAsync(), _ => SelectedResult != null && !IsBusy);
            GenerateAddressesCommand = new RelayCommand(_ => GenerateAddresses(), _ => SelectedResults.Count > 0 && !IsBusy);
            CopyChainCommand = new RelayCommand(_ => CopyChain(), _ => !string.IsNullOrEmpty(ChainJson));
            SetRangeToModuleCommand = new RelayCommand(_ => SetRangeToModule(), _ => SelectedModule != null);

            // Refresh modules to have an up-to-date list
            _scanner.RefreshModules();
            foreach (var m in _scanner.Modules.OrderBy(m => m.Name))
            {
                Modules.Add(m);
            }

            // Set default search range based on pointer size (User Mode)
            RangeStartText = "0x400000";
            if (_scanner.PointerSize == 8)
            {
                RangeEndText = "0x7FFFFFFFFFFF";
            }
            else
            {
                RangeEndText = "0x7FFF0000";
            }

            // Try to find Ares.exe and select it
            SelectedModule = Modules.FirstOrDefault(m => string.Equals(m.Name, "Ares.exe", StringComparison.OrdinalIgnoreCase))
                             ?? Modules.FirstOrDefault();

            if (SelectedModule != null && string.Equals(SelectedModule.Name, "Ares.exe", StringComparison.OrdinalIgnoreCase))
            {
                SetRangeToModule();
            }
        }

        public ObservableCollection<PointerScanResultViewModel> Results { get; } = new();
        public ObservableCollection<PointerScanResultViewModel> SelectedResults { get; } = new();
        public ObservableCollection<PointerScanner.ModuleInfo> Modules { get; } = new();

        public PointerScanResultViewModel? SelectedResult
        {
            get => _selectedResult;
            set
            {
                if (SetProperty(ref _selectedResult, value))
                {
                    ((RelayCommand)ResolveChainCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public PointerScanner.ModuleInfo? SelectedModule
        {
            get => _selectedModule;
            set
            {
                if (SetProperty(ref _selectedModule, value))
                {
                    ((RelayCommand)SetRangeToModuleCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public string TargetAddressText
        {
            get => _targetAddressText;
            set
            {
                if (SetProperty(ref _targetAddressText, value))
                {
                    ((RelayCommand)SuggestCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public string TargetEndText
        {
            get => _targetEndText;
            set => SetProperty(ref _targetEndText, value);
        }

        public string RangeStartText
        {
            get => _rangeStartText;
            set => SetProperty(ref _rangeStartText, value);
        }

        public string RangeEndText
        {
            get => _rangeEndText;
            set => SetProperty(ref _rangeEndText, value);
        }

        public string AlignmentText
        {
            get => _alignmentText;
            set => SetProperty(ref _alignmentText, value);
        }

        public string MaxOffsetText
        {
            get => _maxOffsetText;
            set => SetProperty(ref _maxOffsetText, value);
        }

        public string MaxResultsText
        {
            get => _maxResultsText;
            set => SetProperty(ref _maxResultsText, value);
        }

        public bool ModulesOnly
        {
            get => _modulesOnly;
            set => SetProperty(ref _modulesOnly, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public string ChainJson
        {
            get => _chainJson;
            private set
            {
                if (SetProperty(ref _chainJson, value))
                {
                    ((RelayCommand)CopyChainCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand SuggestCommand { get; }
        public ICommand ScanCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ResolveChainCommand { get; }
        public ICommand GenerateAddressesCommand { get; }
        public ICommand CopyChainCommand { get; }
        public ICommand SetRangeToModuleCommand { get; }

        private void GenerateAddresses()
        {
            if (SelectedResults.Count == 0) return;

            if (!TryParseAddress(TargetAddressText, out ulong targetMin))
            {
                StatusMessage = "Enter valid target address.";
                return;
            }

            ulong finalTarget = targetMin;
            if (!string.IsNullOrWhiteSpace(TargetEndText) && TryParseAddress(TargetEndText, out ulong parsedMax))
            {
                finalTarget = parsedMax;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(ChainJson))
            {
                sb.AppendLine(ChainJson);
                sb.AppendLine();
            }

            foreach (var resVM in SelectedResults)
            {
                var result = resVM.Result;
                if (!result.IsGreen)
                {
                    sb.AppendLine($"// Address 0x{result.PointerAddress:X} is not static, cannot generate [Module+Offset] format.");
                    continue;
                }

                try
                {
                    var chain = PointerScanner.PointerChain.Create(result, finalTarget);
                    var persisted = chain.ToPersisted();
                    
                    // Format: [[[ModuleName + BaseOffset] + Offset1] + Offset2] ... + LastOffset
                    // Each dereference step is wrapped in brackets; only the final offset is plain arithmetic.
                    if (persisted.Offsets.Length >= 2)
                    {
                        string line = $"[{persisted.ModuleName} {FormatOffset(persisted.Offsets[0])}]";
                        for (int i = 1; i < persisted.Offsets.Length - 1; i++)
                        {
                            line = $"[{line} {FormatOffset(persisted.Offsets[i])}]";
                        }
                        line += $" {FormatOffset(persisted.Offsets[^1])}";
                        sb.AppendLine(line);
                    }
                    else if (persisted.Offsets.Length == 1)
                    {
                        sb.AppendLine($"{persisted.ModuleName} {FormatOffset(persisted.Offsets[0])}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"// Error for 0x{result.PointerAddress:X}: {ex.Message}");
                }
            }

            ChainJson = sb.ToString().TrimEnd();
            StatusMessage = $"Generated {SelectedResults.Count} address(es).";
        }

        private static string FormatOffset(long offset)
        {
            if (offset >= 0)
                return $"+ 0x{offset:X}";
            
            return $"- 0x{Math.Abs(offset):X}";
        }

        public void OnSelectionChanged()
        {
            ((RelayCommand)GenerateAddressesCommand).RaiseCanExecuteChanged();
        }

        private void SetRangeToModule()
        {
            if (SelectedModule == null) return;
            RangeStartText = $"0x{SelectedModule.BaseAddress:X}";
            RangeEndText = $"0x{SelectedModule.End:X}";
            StatusMessage = $"Range set to {SelectedModule.Name} (0x{SelectedModule.BaseAddress:X}-0x{SelectedModule.End:X}).";
        }

        private bool CanSuggestRange()
        {
            return TryParseAddress(TargetAddressText, out ulong _);
        }

        private void SuggestRange()
        {
            if (!TryParseAddress(TargetAddressText, out ulong target))
            {
                return;
            }

            var (start, end) = PointerScanner.SuggestTightRangeAround(target);
            RangeStartText = $"0x{start:X}";
            RangeEndText = $"0x{end:X}";
            StatusMessage = $"Suggested ±50MB around 0x{target:X}.";
        }

        private async Task RunScanAsync()
        {
            if (!TryParseAddress(TargetAddressText, out ulong targetMin))
            {
                StatusMessage = "Enter valid hex/decimal target start.";
                return;
            }

            ulong targetMax = targetMin;
            if (!string.IsNullOrWhiteSpace(TargetEndText))
            {
                if (!TryParseAddress(TargetEndText, out targetMax))
                {
                    StatusMessage = "Invalid Target End.";
                    return;
                }
            }

            if (targetMax < targetMin)
            {
                StatusMessage = "Target To must be >= Target From.";
                return;
            }

            ulong start = 0;
            ulong end = 0x7FFFF_FFFF_FFFF; // Default user-mode limit

            bool hasStart = !string.IsNullOrWhiteSpace(RangeStartText);
            bool hasEnd = !string.IsNullOrWhiteSpace(RangeEndText);

            if (hasStart && !TryParseAddress(RangeStartText, out start))
            {
                StatusMessage = "Invalid Start Range.";
                return;
            }

            if (hasEnd && !TryParseAddress(RangeEndText, out end))
            {
                StatusMessage = "Invalid End Range.";
                return;
            }

            if (end <= start)
            {
                StatusMessage = "Range end must be greater than start.";
                return;
            }

            if (!int.TryParse(MaxResultsText, out int maxResults) || maxResults <= 0)
            {
                StatusMessage = "Max results must be > 0.";
                return;
            }

            _scanOptions.MaxResults = maxResults;
            _scanOptions.ModulesOnly = ModulesOnly;
            _scanOptions.Alignment = TryParseAlignment(out int alignment) ? alignment : _scanner.PointerSize;

            if (TryParseOffset(MaxOffsetText, out long maxOffset))
            {
                _scanOptions.MaxOffset = maxOffset;
            }

            ChainJson = string.Empty;
            Results.Clear();
            StatusMessage = "Scanning…";
            IsBusy = true;

            var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            previous?.Cancel();
            previous?.Dispose();
            CancellationToken token = _cts.Token;

            try
            {
                ulong rangeSize = end > start ? end - start : 0;
                if (rangeSize > 512UL * 1024 * 1024 && _scanner.PointerSize == 8)
                {
                    StatusMessage = "Large 64-bit range – prefer tighter windows.";
                }

                var results = await _scanner.ScanAsync(targetMin, targetMax, start, end, _scanOptions, token);

                foreach (var result in results)
                {
                    Results.Add(new PointerScanResultViewModel(result));
                }
                StatusMessage = $"Found {results.Count} pointer(s).";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Scan canceled.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Scan failed: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ResolveChainAsync()
        {
            if (SelectedResult == null)
            {
                return;
            }

            if (!TryParseAddress(TargetAddressText, out ulong targetMin))
            {
                 StatusMessage = "Enter valid target address.";
                 return;
            }

            ulong targetMax = targetMin;
            if (!string.IsNullOrWhiteSpace(TargetEndText) && TryParseAddress(TargetEndText, out ulong parsedMax))
            {
                targetMax = parsedMax;
            }

            // For chain resolution, we need a single final target address to calculate offsets.
            // We use targetMax (the likely variable address) or the pointer's own value + offset if available.
            // Since PointerChain.Create calculates offsets, we just need to provide the "Final Target Address".
            // If the user scanned a range [A, B] and found a pointer to X (where A <= X <= B),
            // The "Target" for the chain is X (the value of the pointer).
            // Actually, PointerChain.Create takes 'finalTarget'.
            // Offset[last] = finalTarget - lastPointer.Value.
            // If lastPointer.Value points to the base of the struct, and we want to reach the field (targetMax),
            // then finalTarget = targetMax.
            // If lastPointer.Value points to the field itself, and finalTarget = targetMax, offset is 0.
            
            // We will use targetMax as the "Goal".
            ulong finalTarget = targetMax;

            IsBusy = true;
            StatusMessage = "Resolving pointer chain…";

            try
            {
                await Task.Run(() =>
                {
                    var result = SelectedResult.Result;
                    if (!result.IsGreen)
                    {
                        // If it's not a module pointer, we can't make a persistent chain unless we find parents.
                        // But since we are resolving *this* result, we assume it IS the chain root.
                        // If the user selected a dynamic pointer, we can't save it as a "Module+Offset" chain.
                        // We could try to scan for parents (Depth+1), but that's what caused the hang.
                        // For now, we only resolve if Green.
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            StatusMessage = "Selected pointer is dynamic (Heap). Cannot resolve static chain.";
                            ChainJson = string.Empty;
                        });
                        return;
                    }

                    try 
                    {
                        var chain = PointerScanner.PointerChain.Create(result, finalTarget);
                        var persisted = chain.ToPersisted();
                        
                        var sb = new StringBuilder();
                        sb.AppendLine("{ ");
                        sb.AppendLine($"  \"moduleName\": \"{persisted.ModuleName}\",");
                        sb.AppendLine($"  \"moduleBaseHint\": \"0x{persisted.ModuleBaseHint:X}\",");
                        sb.AppendLine("  \"offsets\": [" + string.Join(", ", persisted.Offsets.Select(o => $"\"{o}\"")) + "],");
                        sb.AppendLine($"  \"finalTarget\": \"{persisted.FinalTarget}\"");
                        sb.AppendLine("}");
                        
                        var json = sb.ToString();
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            ChainJson = json;
                            StatusMessage = "Chain resolved instantly.";
                        });
                    }
                    catch (Exception ex)
                    {
                         Application.Current.Dispatcher.Invoke(() => StatusMessage = "Error creating chain: " + ex.Message);
                    }
                });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void CopyChain()
        {
            try
            {
                Clipboard.SetText(ChainJson);
                StatusMessage = "Chain JSON copied to clipboard.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Copy failed: " + ex.Message;
            }
        }

        private void CancelScan()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        protected override void OnPropertyChanged(string propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(IsBusy))
            {
                ((RelayCommand)ScanCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ResolveChainCommand).RaiseCanExecuteChanged();
                ((RelayCommand)GenerateAddressesCommand).RaiseCanExecuteChanged();
            }
        }

        private bool TryParseAlignment(out int alignment)
        {
            alignment = 0;
            if (string.IsNullOrWhiteSpace(AlignmentText))
            {
                return false;
            }

            if (int.TryParse(AlignmentText, out int parsed) && parsed > 0)
            {
                alignment = parsed;
                return true;
            }

            if (AlignmentText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(AlignmentText[2..], System.Globalization.NumberStyles.HexNumber, null, out parsed) && parsed > 0)
            {
                alignment = parsed;
                return true;
            }

            return false;
        }

        private static bool TryParseOffset(string text, out long value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);

            return long.TryParse(text, out value);
        }

        private static bool TryParseAddress(string text, out ulong value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
            }

            return ulong.TryParse(text, out value);
        }

        public sealed class PointerScanResultViewModel
        {
            private readonly PointerScanner.PointerScanResult _result;

            public PointerScanResultViewModel(PointerScanner.PointerScanResult result)
            {
                _result = result;
            }

            public string AddressHex => $"0x{_result.PointerAddress:X}";
            public string DistanceHex => $"0x{_result.Distance:X}";
            public string ModuleName => _result.ModuleName ?? "";
            public bool IsGreen => _result.IsGreen;
            public int Depth => _result.Depth;
            public PointerScanner.PointerScanResult Result => _result;
        }
    }
}
