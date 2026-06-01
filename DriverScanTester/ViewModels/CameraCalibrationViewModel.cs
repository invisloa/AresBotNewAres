// <copyright file="CameraCalibrationViewModel.cs" company="DriverScanTester">
//     Copyright (c) DriverScanTester. All rights reserved.
// </copyright>

using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DriverScanTester.Services;
using DriverScanTester.Utils;

namespace DriverScanTester.ViewModels
{
    /// <summary>
    /// View-model for the Camera Calibration window.
    ///
    /// Workflow:
    ///   1. Live-reads the in-game camera angle (0x1aa) every 100 ms.
    ///   2. User faces N in-game → clicks "Capture N" → records the live value.
    ///   3. User turns 90° clockwise → clicks "Capture E" → records.
    ///   4. Repeats for S, W (and optionally N2 at 360°).
    ///   5. Clicks "Apply cardinals" → BearingCalibrationService.SetCardinalMeasured().
    ///   6. Clicks "Save to file" → persists to camera_calibration.json.
    ///   7. Clicks "Reset to defaults" → restores hardcoded values.
    /// </summary>
    public class CameraCalibrationViewModel : BaseViewModel, IDisposable
    {
        private readonly GameMemoryService? _memoryService;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _pollCts = new CancellationTokenSource();
        private readonly Task _pollTask;

        // Live values bound to UI.
        private short _currentCameraAngle;
        private string _statusText = "Attach a process and stand still.";
        private bool _isPolling;
        private string? _editableNorth;
        private string? _editableEast;
        private string? _editableSouth;
        private string? _editableWest;
        private string? _editableNorthFullCircle;
        private string? _editableFullSpin;

        // Auto-measurement (walk-based) state.
        public ObservableCollection<AutoSampleViewModel> AutoSamples { get; }
            = new ObservableCollection<AutoSampleViewModel>
            {
                new AutoSampleViewModel { RequestedBearingDeg = 0f,   Label = "N (0°)" },
                new AutoSampleViewModel { RequestedBearingDeg = 90f,  Label = "E (90°)" },
                new AutoSampleViewModel { RequestedBearingDeg = 180f, Label = "S (180°)" },
                new AutoSampleViewModel { RequestedBearingDeg = 270f, Label = "W (270°)" },
            };
        private bool _isMeasuring;
        private string? _fitResultText;

        public CameraCalibrationViewModel(GameMemoryService? memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;

            CaptureNorthCommand       = new RelayCommand(_ => Capture(0),   _ => _isPolling);
            CaptureEastCommand        = new RelayCommand(_ => Capture(90),  _ => _isPolling);
            CaptureSouthCommand       = new RelayCommand(_ => Capture(180), _ => _isPolling);
            CaptureWestCommand        = new RelayCommand(_ => Capture(270), _ => _isPolling);
            CaptureNorthFullCommand   = new RelayCommand(_ => Capture(360), _ => _isPolling);
            ApplyCardinalsCommand     = new RelayCommand(_ => ApplyCardinals(), _ => HasAllCardinals);
            ApplyFullTableCommand     = new RelayCommand(_ => ApplyFullTable(), _ => HasAllPoints);
            SaveToFileCommand         = new RelayCommand(_ => Save());
            LoadFromFileCommand       = new RelayCommand(_ => Load());
            ResetToDefaultsCommand    = new RelayCommand(_ => ResetDefaults());
            AutoFillTableCommand      = new RelayCommand(_ => AutoFillFromCardinals(), _ => HasAllCardinals);

            // Auto-measurement commands.
            MeasureOneCommand         = new RelayCommand(p => MeasureOne(p), p => !_isMeasuring && _memoryService != null && p is AutoSampleViewModel);
            MeasureAllCommand         = new RelayCommand(async _ => await MeasureAllAsync(), _ => !_isMeasuring && _memoryService != null);
            FitAndApplyCommand        = new RelayCommand(_ => FitAndApply(), _ => !_isMeasuring && HasEnoughAutoSamples);
            ClearAutoSamplesCommand   = new RelayCommand(_ => ClearAutoSamples(), _ => !_isMeasuring);

            // Subscribe to calibration changes so the displayed table refreshes.
            BearingCalibrationService.Changed += OnServiceChanged;

            // Initial sync from current state.
            RefreshFromService();

            // Start the live-polling task.
            _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }

        public ObservableCollection<CalibrationRowViewModel> TableRows { get; } =
            new ObservableCollection<CalibrationRowViewModel>();

        public bool IsMeasuring
        {
            get => _isMeasuring;
            private set
            {
                if (SetProperty(ref _isMeasuring, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string? FitResultText
        {
            get => _fitResultText;
            private set => SetProperty(ref _fitResultText, value);
        }

        public bool HasEnoughAutoSamples
        {
            get
            {
                int ok = 0;
                foreach (var s in AutoSamples) if (s.IsSuccess) ok++;
                return ok >= 2;
            }
        }

        public short CurrentCameraAngle
        {
            get => _currentCameraAngle;
            private set
            {
                if (SetProperty(ref _currentCameraAngle, value))
                {
                    OnPropertyChanged(nameof(CurrentCameraAngleText));
                    OnPropertyChanged(nameof(HasLiveRead));
                }
            }
        }

        public string CurrentCameraAngleText => _currentCameraAngle.ToString();

        public bool HasLiveRead => _currentCameraAngle != 0;

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public bool IsPolling
        {
            get => _isPolling;
            private set
            {
                if (SetProperty(ref _isPolling, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string? EditableNorth
        {
            get => _editableNorth;
            set { if (SetProperty(ref _editableNorth, value)) CommandManager.InvalidateRequerySuggested(); }
        }
        public string? EditableEast
        {
            get => _editableEast;
            set { if (SetProperty(ref _editableEast, value)) CommandManager.InvalidateRequerySuggested(); }
        }
        public string? EditableSouth
        {
            get => _editableSouth;
            set { if (SetProperty(ref _editableSouth, value)) CommandManager.InvalidateRequerySuggested(); }
        }
        public string? EditableWest
        {
            get => _editableWest;
            set { if (SetProperty(ref _editableWest, value)) CommandManager.InvalidateRequerySuggested(); }
        }
        public string? EditableNorthFullCircle
        {
            get => _editableNorthFullCircle;
            set { if (SetProperty(ref _editableNorthFullCircle, value)) CommandManager.InvalidateRequerySuggested(); }
        }
        public string? EditableFullSpin
        {
            get => _editableFullSpin;
            set { if (SetProperty(ref _editableFullSpin, value)) { } }
        }

        public bool HasAllCardinals
            => TryParse(_editableNorth, out _)
            && TryParse(_editableEast, out _)
            && TryParse(_editableSouth, out _)
            && TryParse(_editableWest, out _);

        public bool HasAllPoints
            => HasAllCardinals
            && TryParse(_editableNorthFullCircle, out _);

        public ICommand CaptureNorthCommand { get; }
        public ICommand CaptureEastCommand { get; }
        public ICommand CaptureSouthCommand { get; }
        public ICommand CaptureWestCommand { get; }
        public ICommand CaptureNorthFullCommand { get; }
        public ICommand ApplyCardinalsCommand { get; }
        public ICommand ApplyFullTableCommand { get; }
        public ICommand AutoFillTableCommand { get; }
        public ICommand SaveToFileCommand { get; }
        public ICommand LoadFromFileCommand { get; }
        public ICommand ResetToDefaultsCommand { get; }
        public ICommand MeasureOneCommand { get; }
        public ICommand MeasureAllCommand { get; }
        public ICommand FitAndApplyCommand { get; }
        public ICommand ClearAutoSamplesCommand { get; }

        private void Capture(int bearingDeg)
        {
            if (_memoryService == null)
            {
                StatusText = "Attach a process first.";
                return;
            }

            short angle = _memoryService.GetCameraAngle();
            CurrentCameraAngle = angle;
            string text = angle.ToString();

            switch (bearingDeg)
            {
                case 0:   EditableNorth = text; break;
                case 90:  EditableEast = text; break;
                case 180: EditableSouth = text; break;
                case 270: EditableWest = text; break;
                case 360: EditableNorthFullCircle = text; break;
            }

            StatusText = $"Captured {bearingDeg}° → game angle = {angle}.";
            _log?.Invoke($"[Calib] Captured {bearingDeg}° = {angle}");

            if (HasAllCardinals)
            {
                AutoFillFromCardinals();
            }
        }

        private void AutoFillFromCardinals()
        {
            if (!TryParse(_editableNorth, out float n)) return;
            if (!TryParse(_editableEast, out float e)) return;
            if (!TryParse(_editableSouth, out float s)) return;
            if (!TryParse(_editableWest, out float w)) return;

            float fullSpin = TryParse(_editableNorthFullCircle, out float n2)
                ? n2 - n
                : 4f * (e - n);

            float nAtFull = n + fullSpin;
            float[] values =
            {
                n,
                Lerp(n, e, 1f/3f),   // 30°
                Lerp(n, e, 2f/3f),   // 60°
                e,                   // 90°
                Lerp(e, s, 1f/3f),   // 120°
                Lerp(e, s, 2f/3f),   // 150°
                s,                   // 180°
                Lerp(s, w, 1f/3f),   // 210°
                Lerp(s, w, 2f/3f),   // 240°
                w,                   // 270°
                Lerp(w, nAtFull, 1f/3f), // 300°
                Lerp(w, nAtFull, 2f/3f), // 330°
                nAtFull              // 360°
            };

            for (int i = 0; i < values.Length; i++)
            {
                TableRows[i].GameAngleText = ((short)Math.Round(values[i])).ToString();
            }
            EditableFullSpin = ((short)Math.Round(fullSpin)).ToString();
            StatusText = "Auto-filled 12 intermediate points by linear interpolation.";
        }

        private void ApplyCardinals()
        {
            if (!TryParse(_editableNorth, out float n)) return;
            if (!TryParse(_editableEast, out float e)) return;
            if (!TryParse(_editableSouth, out float s)) return;
            if (!TryParse(_editableWest, out float w)) return;
            float fullSpin = TryParse(_editableNorthFullCircle, out float n2) ? n2 - n : 4f * (e - n);

            BearingCalibrationService.SetCardinalMeasured(n, e, s, w, fullSpin);
            _log?.Invoke($"[Calib] Applied cardinals N={n} E={e} S={s} W={w} fullSpin={fullSpin}");
            StatusText = "Calibration applied. New bearing→angle mapping is now live.";
        }

        private void ApplyFullTable()
        {
            // Use the values currently shown in the table (manual or auto-filled).
            var newAngles = new float[BearingCalibrationService.PointCount];
            for (int i = 0; i < TableRows.Count; i++)
            {
                if (!TryParse(TableRows[i].GameAngleText, out float v))
                {
                    StatusText = $"Invalid value at row {i}.";
                    return;
                }
                newAngles[i] = v;
            }
            BearingCalibrationService.ReplaceAll(newAngles);
            StatusText = "Full 13-point table applied.";
        }

        private void Save()
        {
            try
            {
                BearingCalibrationService.SaveToFile();
                _log?.Invoke($"[Calib] Saved calibration to {BearingCalibrationService.DefaultFilePath}");
                StatusText = $"Saved to {BearingCalibrationService.DefaultFilePath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Save failed: {ex.Message}";
                _log?.Invoke($"[Calib] Save failed: {ex.Message}");
            }
        }

        // ─── Auto-measurement (walk-based) ────────────────────────────────────

        private async void MeasureOne(object? param)
        {
            if (param is not AutoSampleViewModel sample) return;
            if (_memoryService == null)
            {
                StatusText = "Attach a process first.";
                return;
            }

            IsMeasuring = true;
            sample.IsRunning = true;
            sample.StatusText = "Walking…";
            StatusText = $"Measuring {sample.Label}…";
            try
            {
                var walker = new CalibrationWalker(_memoryService, _log);
                var result = await walker.MeasureAsync(
                    sample.RequestedBearingDeg, walkDurationMs: 1500, sampleIntervalMs: 50);

                sample.IsSuccess = result.Success;
                sample.ActualBearingDeg = result.ActualBearingDeg;
                sample.AvgGameAngle = result.AvgGameAngle;
                sample.SampleCount = result.SampleCount;
                sample.DistanceTravelled = result.DistanceTravelled;
                sample.StatusText = result.Success
                    ? $"Δdist={result.DistanceTravelled:F1}  n={result.SampleCount}"
                    : (string.IsNullOrEmpty(result.Note) ? "failed" : result.Note);

                OnPropertyChanged(nameof(HasEnoughAutoSamples));
                CommandManager.InvalidateRequerySuggested();

                StatusText = result.Success
                    ? $"{sample.Label}: asked {result.RequestedBearingDeg:F0}°, actually walked {result.ActualBearingDeg:F1}° at game_angle={result.AvgGameAngle:F0} (n={result.SampleCount})."
                    : $"{sample.Label}: FAILED — {result.Note}";

                _log?.Invoke($"[Calib-Auto] {StatusText}");
            }
            catch (Exception ex)
            {
                sample.StatusText = $"error: {ex.Message}";
                StatusText = $"Measurement error: {ex.Message}";
            }
            finally
            {
                sample.IsRunning = false;
                IsMeasuring = false;
            }
        }

        private async Task MeasureAllAsync()
        {
            if (_memoryService == null) return;
            IsMeasuring = true;
            StatusText = "Running all 4 measurements (N, E, S, W) — about 6 seconds…";
            try
            {
                // Pause the live polling task while we drive W manually, so it
                // doesn't fight us.
                // (Poll task only reads camera angle, not W, so it's safe to leave on.)
                for (int i = 0; i < AutoSamples.Count; i++)
                {
                    var s = AutoSamples[i];
                    StatusText = $"({i + 1}/{AutoSamples.Count}) Measuring {s.Label}…";
                    s.IsRunning = true;
                    s.StatusText = "Walking…";
                    try
                    {
                        var walker = new CalibrationWalker(_memoryService, _log);
                        var result = await walker.MeasureAsync(
                            s.RequestedBearingDeg, walkDurationMs: 1500, sampleIntervalMs: 50);

                        s.IsSuccess = result.Success;
                        s.ActualBearingDeg = result.ActualBearingDeg;
                        s.AvgGameAngle = result.AvgGameAngle;
                        s.SampleCount = result.SampleCount;
                        s.DistanceTravelled = result.DistanceTravelled;
                        s.StatusText = result.Success
                            ? $"Δdist={result.DistanceTravelled:F1}  n={result.SampleCount}"
                            : (string.IsNullOrEmpty(result.Note) ? "failed" : result.Note);

                        _log?.Invoke($"[Calib-Auto] {s.Label}: asked {result.RequestedBearingDeg:F0}°, walked {result.ActualBearingDeg:F1}° at game_angle={result.AvgGameAngle:F0} (n={result.SampleCount}){(result.Success ? "" : " — FAILED: " + result.Note)}");
                    }
                    catch (Exception ex)
                    {
                        s.StatusText = $"error: {ex.Message}";
                        _log?.Invoke($"[Calib-Auto] {s.Label} threw: {ex.Message}");
                    }
                    finally
                    {
                        s.IsRunning = false;
                    }

                    OnPropertyChanged(nameof(HasEnoughAutoSamples));
                    CommandManager.InvalidateRequerySuggested();

                    // Small gap between measurements so the bot fully stops
                    // and the camera stabilises before the next pass.
                    await Task.Delay(400);
                }

                int good = 0;
                foreach (var s in AutoSamples) if (s.IsSuccess) good++;
                StatusText = $"Done. {good}/{AutoSamples.Count} measurements succeeded. Click 'Fit and apply' to update the calibration.";

                if (good >= 2)
                {
                    FitAndApply();
                }
            }
            finally
            {
                IsMeasuring = false;
            }
        }

        private void FitAndApply()
        {
            var xs = new List<float>();
            var ys = new List<float>();
            foreach (var s in AutoSamples)
            {
                if (!s.IsSuccess) continue;
                xs.Add(s.ActualBearingDeg);
                ys.Add(s.AvgGameAngle);
            }
            if (xs.Count < 2)
            {
                StatusText = "Need at least 2 successful samples to fit.";
                return;
            }

            if (!BearingCalibrationService.FitLinear(xs, ys, out var fit))
            {
                StatusText = "Fit failed (bearings too similar or invalid).";
                return;
            }

            FitResultText =
                $"scale={fit.Scale:F4} game-units/°  ·  offset={fit.Offset:F1}  ·  full spin={fit.FullSpin:F2}  ·  mean |resid|={fit.MeanResidual:F2}  ·  n={fit.PointCount}";
            StatusText = "Fit applied. New calibration is live. Click 'Save to file' to persist.";
            _log?.Invoke($"[Calib-Auto] {FitResultText}");
        }

        private void ClearAutoSamples()
        {
            foreach (var s in AutoSamples)
            {
                s.IsSuccess = false;
                s.ActualBearingDeg = 0f;
                s.AvgGameAngle = 0f;
                s.SampleCount = 0;
                s.DistanceTravelled = 0f;
                s.StatusText = "";
            }
            FitResultText = null;
            OnPropertyChanged(nameof(HasEnoughAutoSamples));
            CommandManager.InvalidateRequerySuggested();
            StatusText = "Auto-samples cleared.";
        }

        private void Load()
        {
            if (BearingCalibrationService.LoadFromFile())
            {
                _log?.Invoke($"[Calib] Loaded calibration from {BearingCalibrationService.DefaultFilePath}");
                StatusText = $"Loaded from {BearingCalibrationService.DefaultFilePath}";
            }
            else
            {
                StatusText = "No saved calibration file found.";
            }
        }

        private void ResetDefaults()
        {
            BearingCalibrationService.ResetToDefaults();
            _log?.Invoke("[Calib] Reset to hardcoded defaults.");
            StatusText = "Restored hardcoded defaults.";
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            IsPolling = true;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_memoryService != null)
                    {
                        try
                        {
                            short angle = _memoryService.GetCameraAngle();
                            // Marshal back to UI thread for property updates.
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CurrentCameraAngle = angle);
                        }
                        catch
                        {
                            // Swallow transient read errors.
                        }
                    }
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            finally
            {
                IsPolling = false;
            }
        }

        private void RefreshFromService()
        {
            var points = BearingCalibrationService.Points;
            TableRows.Clear();
            foreach (var p in points)
            {
                TableRows.Add(new CalibrationRowViewModel
                {
                    BearingDeg = p.BearingDeg,
                    GameAngleText = ((short)Math.Round(p.GameAngle)).ToString()
                });
            }

            EditableNorth            = ((short)Math.Round(points[0].GameAngle)).ToString();
            EditableEast             = ((short)Math.Round(points[3].GameAngle)).ToString();
            EditableSouth            = ((short)Math.Round(points[6].GameAngle)).ToString();
            EditableWest             = ((short)Math.Round(points[9].GameAngle)).ToString();
            EditableNorthFullCircle  = ((short)Math.Round(points[12].GameAngle)).ToString();
            EditableFullSpin         = ((short)Math.Round(BearingCalibrationService.FullSpinGameUnits)).ToString();
            StatusText               = BearingCalibrationService.IsOverridden
                ? "Loaded override values."
                : "Showing hardcoded defaults.";
        }

        private void OnServiceChanged(object? sender, EventArgs e) => RefreshFromService();

        private static bool TryParse(string? s, out float v)
        {
            if (!string.IsNullOrWhiteSpace(s)
                && float.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                return true;
            }
            v = 0f;
            return false;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public void Dispose()
        {
            try
            {
                _pollCts.Cancel();
                _pollTask.Wait(500);
            }
            catch { }
            _pollCts.Dispose();
            BearingCalibrationService.Changed -= OnServiceChanged;
        }
    }

    /// <summary>Single row in the calibration table (editable in the UI).</summary>
    public class CalibrationRowViewModel : BaseViewModel
    {
        private float _bearingDeg;
        private string? _gameAngleText;

        public float BearingDeg
        {
            get => _bearingDeg;
            set => SetProperty(ref _bearingDeg, value);
        }

        public string? GameAngleText
        {
            get => _gameAngleText;
            set => SetProperty(ref _gameAngleText, value);
        }
    }

    /// <summary>One auto-measurement sample (N, E, S or W by default).</summary>
    public class AutoSampleViewModel : BaseViewModel
    {
        private string _label = "";
        private float _requestedBearingDeg;
        private bool _isRunning;
        private bool _isSuccess;
        private float _actualBearingDeg;
        private float _avgGameAngle;
        private int _sampleCount;
        private float _distanceTravelled;
        private string _statusText = "";

        public string Label
        {
            get => _label;
            set => SetProperty(ref _label, value);
        }
        public float RequestedBearingDeg
        {
            get => _requestedBearingDeg;
            set => SetProperty(ref _requestedBearingDeg, value);
        }
        public bool IsRunning
        {
            get => _isRunning;
            set => SetProperty(ref _isRunning, value);
        }
        public bool IsSuccess
        {
            get => _isSuccess;
            set => SetProperty(ref _isSuccess, value);
        }
        public float ActualBearingDeg
        {
            get => _actualBearingDeg;
            set => SetProperty(ref _actualBearingDeg, value);
        }
        public float AvgGameAngle
        {
            get => _avgGameAngle;
            set => SetProperty(ref _avgGameAngle, value);
        }
        public int SampleCount
        {
            get => _sampleCount;
            set => SetProperty(ref _sampleCount, value);
        }
        public float DistanceTravelled
        {
            get => _distanceTravelled;
            set => SetProperty(ref _distanceTravelled, value);
        }
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }
    }
}
