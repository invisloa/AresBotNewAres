using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.Utils;
using Microsoft.Win32;

namespace DriverScanTester.ViewModels
{
    public class PathEditorViewModel : BaseViewModel
    {
        private const string SAVE_DIR = "SavedPaths";

        // --- Editor State (Tab 1) ---
        private ObservableCollection<PathPoint> _points = new ObservableCollection<PathPoint>();
        public ObservableCollection<PathPoint> Points
        {
            get => _points;
            set => SetProperty(ref _points, value);
        }

        private PathPoint? _selectedPoint;
        public PathPoint? SelectedPoint
        {
            get => _selectedPoint;
            set => SetProperty(ref _selectedPoint, value);
        }

        private string _segmentName = "NewSegment";
        public string SegmentName
        {
            get => _segmentName;
            set => SetProperty(ref _segmentName, value);
        }

        private MovementPrecision _segmentPrecision = MovementPrecision.Medium;
        public MovementPrecision SegmentPrecision
        {
            get => _segmentPrecision;
            set => SetProperty(ref _segmentPrecision, value);
        }

        private BotMode _segmentBotMode = BotMode.OnlyMove;
        public BotMode SegmentBotMode
        {
            get => _segmentBotMode;
            set => SetProperty(ref _segmentBotMode, value);
        }

        // --- Route Builder State (Tab 2) ---
        private ObservableCollection<string> _availableSegments = new ObservableCollection<string>();
        public ObservableCollection<string> AvailableSegments
        {
            get => _availableSegments;
            set => SetProperty(ref _availableSegments, value);
        }

        private string? _selectedAvailableSegment;
        public string? SelectedAvailableSegment
        {
            get => _selectedAvailableSegment;
            set => SetProperty(ref _selectedAvailableSegment, value);
        }

        private ObservableCollection<string> _routeSegments = new ObservableCollection<string>();
        public ObservableCollection<string> RouteSegments
        {
            get => _routeSegments;
            set => SetProperty(ref _routeSegments, value);
        }

        private string? _selectedRouteSegment;
        public string? SelectedRouteSegment
        {
            get => _selectedRouteSegment;
            set => SetProperty(ref _selectedRouteSegment, value);
        }

        private bool _loopRoute = false;
        public bool LoopRoute
        {
            get => _loopRoute;
            set => SetProperty(ref _loopRoute, value);
        }

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        // --- Dependencies ---
        private readonly Func<uint, ulong, byte[], uint, bool> _readFunc;
        private readonly Func<int> _pointerSizeFunc;
        private readonly Func<ulong> _moduleBaseFunc;
        private readonly uint _pid;

        // --- Commands ---
        // Editor
        public ICommand AddPointCommand { get; }
        public ICommand RemovePointCommand { get; }
        public ICommand MovePointUpCommand { get; }
        public ICommand MovePointDownCommand { get; }
        public ICommand CapturePositionCommand { get; }
        public ICommand ClearPathCommand { get; }
        public ICommand SaveSegmentCommand { get; }
        public ICommand LoadSegmentCommand { get; } // Load into editor
        public ICommand DeleteSegmentCommand { get; }
        public ICommand RunCurrentSegmentCommand { get; }

        // Route Builder
        public ICommand RefreshLibraryCommand { get; }
        public ICommand AddToRouteCommand { get; }
        public ICommand RemoveFromRouteCommand { get; }
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand RunFullRouteCommand { get; }
        public ICommand ClearRouteCommand { get; }
        public ICommand StopBotCommand { get; }

        public event Action<List<DriverScanTester.Services.Waypoint>, bool>? OnRunPath;
        public Action? OnStopBot;

        // Offsets
        private const ulong PlayerPtrOffset = 0x471C88;
        private const ulong XOffset = 0x144;
        private const ulong YOffset = 0xEE8;

        public PathEditorViewModel(Func<uint, ulong, byte[], uint, bool> readFunc, Func<int> ptrSizeFunc, Func<ulong> modBaseFunc, uint pid)
        {
            _readFunc = readFunc;
            _pointerSizeFunc = ptrSizeFunc;
            _moduleBaseFunc = modBaseFunc;
            _pid = pid;

            // Editor Commands
            AddPointCommand = new RelayCommand(_ => AddPoint());
            RemovePointCommand = new RelayCommand(_ => RemovePoint(), _ => SelectedPoint != null);
            MovePointUpCommand = new RelayCommand(_ => MovePoint(-1), _ => SelectedPoint != null);
            MovePointDownCommand = new RelayCommand(_ => MovePoint(1), _ => SelectedPoint != null);
            CapturePositionCommand = new RelayCommand(_ => CapturePosition());
            ClearPathCommand = new RelayCommand(_ => Points.Clear());
            SaveSegmentCommand = new RelayCommand(_ => SaveSegment());
            LoadSegmentCommand = new RelayCommand(_ => LoadSegmentIntoEditor(), _ => !string.IsNullOrEmpty(SelectedAvailableSegment));
            DeleteSegmentCommand = new RelayCommand(_ => DeleteSegment(), _ => !string.IsNullOrEmpty(SelectedAvailableSegment));
            RunCurrentSegmentCommand = new RelayCommand(_ => RunEditorPath(), _ => Points.Count > 0);

            // Route Commands
            RefreshLibraryCommand = new RelayCommand(_ => RefreshLibrary());
            AddToRouteCommand = new RelayCommand(_ => AddToRoute(), _ => !string.IsNullOrEmpty(SelectedAvailableSegment));
            RemoveFromRouteCommand = new RelayCommand(_ => RemoveFromRoute(), _ => SelectedRouteSegment != null);
            MoveUpCommand = new RelayCommand(_ => MoveRouteItem(-1), _ => SelectedRouteSegment != null);
            MoveDownCommand = new RelayCommand(_ => MoveRouteItem(1), _ => SelectedRouteSegment != null);
            RunFullRouteCommand = new RelayCommand(_ => RunCombinedRoute(), _ => RouteSegments.Count > 0);
            ClearRouteCommand = new RelayCommand(_ => RouteSegments.Clear());
            StopBotCommand = new RelayCommand(_ => OnStopBot?.Invoke());

            // Init
            if (!Directory.Exists(SAVE_DIR)) Directory.CreateDirectory(SAVE_DIR);
            RefreshLibrary();
        }

        // ========================== EDITOR LOGIC ==========================

        private void AddPoint()
        {
            Points.Add(new PathPoint(0, 0, SegmentPrecision, SegmentBotMode));
            StatusText = "Added new point (0,0).";
        }

        private void RemovePoint()
        {
            if (SelectedPoint != null)
            {
                Points.Remove(SelectedPoint);
            }
        }

        private void MovePoint(int direction)
        {
            if (SelectedPoint == null) return;

            int oldIndex = Points.IndexOf(SelectedPoint);
            int newIndex = oldIndex + direction;

            if (newIndex >= 0 && newIndex < Points.Count)
            {
                Points.Move(oldIndex, newIndex);
            }
        }

        private void CapturePosition()
        {
            ulong moduleBase = _moduleBaseFunc();
            int ptrSize = _pointerSizeFunc();

            if (moduleBase == 0 || ptrSize == 0)
            {
                StatusText = "Error: Invalid module base or pointer size.";
                return;
            }

            // 1. Read Player Ptr
            ulong ptrAddr = moduleBase + PlayerPtrOffset;
            byte[] buf = new byte[ptrSize];
            if (!_readFunc(_pid, ptrAddr, buf, 0)) 
            {
                StatusText = "Error: Failed to read player pointer address.";
                return;
            }

            ulong playerBase = (ptrSize == 4) ? BitConverter.ToUInt32(buf, 0) : BitConverter.ToUInt64(buf, 0);
            if (playerBase == 0)
            {
                StatusText = "Error: Player base is 0.";
                return;
            }

            // 2. Read X, Y
            byte[] xBuf = new byte[2];
            byte[] yBuf = new byte[2];

            // Helper wrapper for read
            bool Read(ulong addr, byte[] b) => _readFunc(_pid, addr, b, 0);

            if (Read(playerBase + XOffset, xBuf) && Read(playerBase + YOffset, yBuf))
            {
                float x = (float)BitConverter.ToInt16(xBuf, 0);
                float y = (float)BitConverter.ToInt16(yBuf, 0);
                Points.Add(new PathPoint(x, y, SegmentPrecision, SegmentBotMode));
                StatusText = $"Captured ({x}, {y}) with {SegmentPrecision} precision and {SegmentBotMode}.";
            }
            else
            {
                StatusText = "Error: Failed to read position.";
            }
        }

        private void SaveSegment()
        {
            if (Points.Count == 0)
            {
                StatusText = "No points to save.";
                return;
            }

            string cleanName = SegmentName.Trim();
            if (string.IsNullOrEmpty(cleanName))
            {
                StatusText = "Enter a segment name.";
                return;
            }

            if (!cleanName.EndsWith(".json")) cleanName += ".json";

            string path = Path.Combine(SAVE_DIR, cleanName);

            try
            {
                var segment = new PathSegment
                {
                    Name = SegmentName,
                    Precision = SegmentPrecision,
                    Mode = SegmentBotMode,
                    Points = Points.ToList()
                };

                string json = JsonSerializer.Serialize(segment);
                File.WriteAllText(path, json);
                StatusText = $"Saved '{cleanName}' ({Points.Count} points with per-point precision/mode).";
                RefreshLibrary();
            }
            catch (Exception ex)
            {
                StatusText = "Error saving: " + ex.Message;
            }
        }

        private void DeleteSegment()
        {
            if (string.IsNullOrEmpty(SelectedAvailableSegment)) return;

            var result = MessageBox.Show($"Are you sure you want to delete '{SelectedAvailableSegment}'?", 
                                       "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    string path = Path.Combine(SAVE_DIR, SelectedAvailableSegment);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        StatusText = $"Deleted '{SelectedAvailableSegment}'.";
                        RefreshLibrary();
                    }
                }
                catch (Exception ex)
                {
                    StatusText = "Error deleting: " + ex.Message;
                }
            }
        }

        private void LoadSegmentIntoEditor()
        {
            if (string.IsNullOrEmpty(SelectedAvailableSegment)) return;

            string path = Path.Combine(SAVE_DIR, SelectedAvailableSegment);
            if (!File.Exists(path))
            {
                StatusText = "File not found.";
                RefreshLibrary();
                return;
            }

            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<PathSegment>(json);
                if (loaded != null)
                {
                    Points = new ObservableCollection<PathPoint>(loaded.Points);
                    SegmentName = loaded.Name;
                    SegmentPrecision = loaded.Precision;
                    SegmentBotMode = loaded.Mode;
                    StatusText = $"Loaded '{SelectedAvailableSegment}' ({SegmentPrecision}/{SegmentBotMode}) into Editor.";
                }
            }
            catch (Exception ex)
            {
                StatusText = "Error loading: " + ex.Message;
            }
        }

        private void RunEditorPath()
        {
            var list = Points.Select(p => new DriverScanTester.Services.Waypoint(p.X, p.Y, p.Precision, p.Mode)).ToList();
            OnRunPath?.Invoke(list, LoopRoute);
            StatusText = "Running current editor segment...";
        }

        // ========================== ROUTE BUILDER LOGIC ==========================

        private void RefreshLibrary()
        {
            try
            {
                AvailableSegments.Clear();
                if (!Directory.Exists(SAVE_DIR)) Directory.CreateDirectory(SAVE_DIR);

                var files = Directory.GetFiles(SAVE_DIR, "*.json");
                foreach (var f in files)
                {
                    AvailableSegments.Add(Path.GetFileName(f));
                }
            }
            catch (Exception ex)
            {
                StatusText = "Library Error: " + ex.Message;
            }
        }

        private void AddToRoute()
        {
            if (!string.IsNullOrEmpty(SelectedAvailableSegment))
            {
                RouteSegments.Add(SelectedAvailableSegment);
            }
        }

        private void RemoveFromRoute()
        {
            if (SelectedRouteSegment != null)
            {
                RouteSegments.Remove(SelectedRouteSegment);
            }
        }

        private void MoveRouteItem(int direction)
        {
            if (SelectedRouteSegment == null) return;
            
            int oldIndex = RouteSegments.IndexOf(SelectedRouteSegment);
            int newIndex = oldIndex + direction;

            if (newIndex >= 0 && newIndex < RouteSegments.Count)
            {
                RouteSegments.Move(oldIndex, newIndex);
            }
        }

        private void RunCombinedRoute()
        {
            if (RouteSegments.Count == 0) return;

            var combinedWaypoints = new List<DriverScanTester.Services.Waypoint>();
            int segmentsLoaded = 0;

            foreach (var segName in RouteSegments)
            {
                string path = Path.Combine(SAVE_DIR, segName);
                if (File.Exists(path))
                {
                    try
                    {
                        string json = File.ReadAllText(path);
                        var loaded = JsonSerializer.Deserialize<PathSegment>(json);
                        if (loaded != null)
                        {
                            combinedWaypoints.AddRange(loaded.Points.Select(p => new DriverScanTester.Services.Waypoint(p.X, p.Y, p.Precision, p.Mode)));
                            segmentsLoaded++;
                        }
                    }
                    catch 
                    {
                        StatusText = $"Error reading segment: {segName}";
                    }
                }
            }

            if (combinedWaypoints.Count > 0)
            {
                OnRunPath?.Invoke(combinedWaypoints, LoopRoute);
                StatusText = $"Running Route: {segmentsLoaded} segments, {combinedWaypoints.Count} points total.";
            }
            else
            {
                StatusText = "Combined route resulted in 0 points.";
            }
        }
    }
}