using DriverScanTester.Memory;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace DriverScanTester.Views
{
    /// <summary>
    /// Modal dialog that drives the pointer scan workflow. Instantiate with a configured <see cref="PointerScanner"/> and
    /// optionally a callback that consumes the selected pointer hit (e.g. to queue persistence of a pointer chain).
    /// </summary>
    public partial class PointerScanDialog : Window
    {
        private readonly PointerScanner _pointerScanner;
        private readonly ulong _targetAddress;
        private readonly int _pointerSize;
        private readonly Action<string>? _log;
        private readonly Action<PointerScanResult>? _onUseSelected;
        private readonly List<PointerScanResult> _results = new();
        private CancellationTokenSource? _cts;
        private bool _isScanning;
        private const int DefaultMaxResults = 5000;

        private sealed class ResultView
        {
            public ResultView(PointerScanResult result)
            {
                Result = result;
                PointerAddressDisplay = $"0x{result.PointerAddress:X}";
                ModuleDisplay = result.IsGreen ? result.ModuleName ?? string.Empty : string.Empty;
                DistanceDisplay = $"0x{result.Distance:X}";
                IsGreen = result.IsGreen;
            }

            public PointerScanResult Result { get; }
            public string PointerAddressDisplay { get; }
            public string ModuleDisplay { get; }
            public string DistanceDisplay { get; }
            public bool IsGreen { get; }
        }

        public PointerScanDialog(PointerScanner pointerScanner,
                                 ulong targetAddress,
                                 int pointerSize,
                                 Action<string>? log,
                                 Action<PointerScanResult>? onUseSelected)
        {
            InitializeComponent();
            _pointerScanner = pointerScanner ?? throw new ArgumentNullException(nameof(pointerScanner));
            _targetAddress = targetAddress;
            _pointerSize = pointerSize;
            _log = log;
            _onUseSelected = onUseSelected;

            TargetAddressTextBox.Text = $"0x{_targetAddress:X}";
            RangeStartTextBox.Text = $"0x{_targetAddress:X}";

            ulong defaultEnd;
            try
            {
                defaultEnd = checked(_targetAddress + (ulong)Math.Max(_pointerSize, 0x1000));
            }
            catch (OverflowException)
            {
                defaultEnd = ulong.MaxValue;
            }

            RangeEndTextBox.Text = $"0x{defaultEnd:X}";
            AlignmentTextBox.Text = _pointerSize.ToString(CultureInfo.InvariantCulture);
            StatusTextBlock.Text = "Configure the scan options and click Scan.";
        }

        private void SuggestButton_Click(object sender, RoutedEventArgs e)
        {
            const ulong windowSize = 50UL * 1024UL * 1024UL;
            ulong start = _targetAddress > windowSize ? _targetAddress - windowSize : 0UL;
            ulong end;
            try
            {
                checked
                {
                    end = _targetAddress + windowSize;
                }
            }
            catch (OverflowException)
            {
                end = ulong.MaxValue;
            }

            RangeStartTextBox.Text = $"0x{start:X}";
            RangeEndTextBox.Text = $"0x{end:X}";
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
            {
                _cts?.Cancel();
                return;
            }

            if (!TryBuildRequest(out var request))
                return;

            _cts = new CancellationTokenSource();
            _isScanning = true;
            UpdateScanUi();
            StatusTextBlock.Text = "Scanning…";
            _log?.Invoke($"Pointer scan started for 0x{_targetAddress:X} in [0x{request.RangeStart:X}, 0x{request.RangeEnd:X}).");

            try
            {
                var results = await _pointerScanner.ScanAsync(request, _cts.Token).ConfigureAwait(true);
                _results.Clear();
                _results.AddRange(results.Results);
                ApplyFilter();

                string status = $"Found {_results.Count} pointer(s).";
                if (results.WasTruncated)
                    status += $" Showing first {request.MaxResults}.";
                StatusTextBlock.Text = status;
                _log?.Invoke(results.WasTruncated
                    ? $"Pointer scan complete with {_results.Count} matches (truncated)."
                    : $"Pointer scan complete with {_results.Count} matches.");
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "Scan canceled.";
                _log?.Invoke("Pointer scan canceled.");
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = "Scan failed.";
                MessageBox.Show(this, ex.Message, "Pointer scan", MessageBoxButton.OK, MessageBoxImage.Error);
                _log?.Invoke("Pointer scan failed: " + ex.Message);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _isScanning = false;
                UpdateScanUi();
            }
        }

        private bool TryBuildRequest(out PointerScanRequest request)
        {
            request = null!;

            if (!TryParseAddress(RangeStartTextBox.Text, out ulong rangeStart))
            {
                MessageBox.Show(this, "Invalid range start.", "Pointer scan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!TryParseAddress(RangeEndTextBox.Text, out ulong rangeEnd))
            {
                MessageBox.Show(this, "Invalid range end.", "Pointer scan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (rangeEnd <= rangeStart)
            {
                MessageBox.Show(this, "Range end must be greater than range start.", "Pointer scan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            int alignment = _pointerSize;
            string alignmentText = AlignmentTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(alignmentText))
            {
                if (!TryParseInteger(alignmentText, out alignment) || alignment <= 0)
                {
                    MessageBox.Show(this, "Alignment must be a positive integer.", "Pointer scan", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            request = new PointerScanRequest
            {
                TargetAddress = _targetAddress,
                RangeStart = rangeStart,
                RangeEnd = rangeEnd,
                Alignment = alignment,
                GreenOnly = GreenOnlyCheckBox.IsChecked == true,
                MaxResults = DefaultMaxResults
            };

            return true;
        }

        private void ApplyFilter()
        {
            IEnumerable<PointerScanResult> filtered = _results;
            if (GreenOnlyCheckBox.IsChecked == true)
                filtered = filtered.Where(r => r.IsGreen);

            var view = filtered.Select(r => new ResultView(r)).ToList();
            ResultsGrid.ItemsSource = view;
            if (view.Count > 0)
            {
                ResultsGrid.SelectedIndex = 0;
            }
            else
            {
                ResultsGrid.SelectedIndex = -1;
            }
            UseSelectedButton.IsEnabled = view.Count > 0 && ResultsGrid.SelectedItem != null;
        }

        private static bool TryParseAddress(string text, out ulong value)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text[2..];
            return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseInteger(string text, out int value)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private void UpdateScanUi()
        {
            if (_isScanning)
            {
                ScanButton.Content = "Cancel";
                UseSelectedButton.IsEnabled = false;
                RangeStartTextBox.IsEnabled = false;
                RangeEndTextBox.IsEnabled = false;
                AlignmentTextBox.IsEnabled = false;
                SuggestButton.IsEnabled = false;
                GreenOnlyCheckBox.IsEnabled = false;
            }
            else
            {
                ScanButton.Content = "Scan";
                RangeStartTextBox.IsEnabled = true;
                RangeEndTextBox.IsEnabled = true;
                AlignmentTextBox.IsEnabled = true;
                SuggestButton.IsEnabled = true;
                GreenOnlyCheckBox.IsEnabled = true;
                ResultsGrid_SelectionChanged(null!, null!);
            }
        }

        private void GreenOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
                return;

            ApplyFilter();
        }

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isScanning)
            {
                UseSelectedButton.IsEnabled = false;
                return;
            }

            UseSelectedButton.IsEnabled = ResultsGrid.SelectedItem is ResultView;
        }

        private void UseSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is not ResultView view)
                return;

            Clipboard.SetText(view.PointerAddressDisplay);
            _onUseSelected?.Invoke(view.Result);
            _log?.Invoke($"Pointer selected: {view.PointerAddressDisplay} -> 0x{view.Result.PointsTo:X}." +
                (view.Result.IsGreen ? $" Module: {view.Result.ModuleName}" : ""));
            DialogResult = true;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (_isScanning)
            {
                _cts?.Cancel();
            }
        }
    }
}
