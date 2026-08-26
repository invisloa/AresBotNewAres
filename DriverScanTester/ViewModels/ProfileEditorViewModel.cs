using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.Utils;

namespace DriverScanTester.ViewModels
{
    /// <summary>
    /// ViewModel for the Bot Profile editor tab in PathEditorWindow.
    /// A profile is a linear FLOW of mixed steps (Path / Repot / Operation / ExpLoop)
    /// that the bot cycles through. Any step type can be placed anywhere in the flow.
    /// Uses BotProfileLoader as the single persistence and validation service.
    /// </summary>
    public class ProfileEditorViewModel : BaseViewModel
    {
        // Used only for deleting profile files (persistence itself lives in BotProfileLoader).
        private static readonly string PROFILE_DIR = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedBotProfiles"));
        private static readonly string PATH_DIR = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));

        private readonly Action<string> _log;
        private readonly BotProfileLoader _profileLoader;

        /// <summary>
        /// Reads the current in-game player state for the "capture start position"
        /// button: (X, Y) position and current map number. Null when the editor has no
        /// game access (then the capture button stays disabled).
        /// </summary>
        private readonly Func<(float X, float Y, int Map, bool Success)>? _capturePosition;

        public ProfileEditorViewModel(
            Action<string>? log = null,
            Func<(float X, float Y, int Map, bool Success)>? capturePosition = null)
        {
            _log = log ?? (_ => { });
            _capturePosition = capturePosition;
            _profileLoader = new BotProfileLoader(_log);

            if (!Directory.Exists(PATH_DIR))
                Directory.CreateDirectory(PATH_DIR);

            // Commands
            NewProfileCommand = new RelayCommand(_ => NewProfile());
            SaveProfileCommand = new RelayCommand(_ => SaveProfile());
            LoadProfileCommand = new RelayCommand(_ => LoadSelectedProfile(), _ => SelectedProfileName != null);
            DeleteProfileCommand = new RelayCommand(_ => DeleteSelectedProfile(), _ => SelectedProfileName != null);
            RefreshProfilesCommand = new RelayCommand(_ => RefreshProfiles());
            RefreshPathsCommand = new RelayCommand(_ => RefreshPaths());

            AddFlowStepCommand = new RelayCommand(_ => AddFlowStep(), _ => CurrentProfile != null);
            RemoveFlowStepCommand = new RelayCommand(_ => RemoveFlowStep(), _ => CanRemoveFlowStep);
            MoveFlowStepUpCommand = new RelayCommand(_ => MoveFlowStep(-1), _ => CanMoveFlowStep(-1));
            MoveFlowStepDownCommand = new RelayCommand(_ => MoveFlowStep(1), _ => CanMoveFlowStep(1));

            AddRouteCommand = new RelayCommand(_ => AddRoute(), _ => CanAddRoute);
            RemoveRouteCommand = new RelayCommand(_ => RemoveRoute(), _ => CanRemoveRoute);
            CaptureStartPositionCommand = new RelayCommand(_ => CaptureStartPosition(), _ => CanCaptureStartPosition);

            // Initial loads
            BuildAvailableOperations();
            RefreshProfiles();
            RefreshPaths();
        }

        // ──────────────────── Profile list ────────────────────

        private ObservableCollection<string> _profileNames = new();
        public ObservableCollection<string> ProfileNames
        {
            get => _profileNames;
            set => SetProperty(ref _profileNames, value);
        }

        // While true, SelectedProfileName changes from RefreshProfiles() must not trigger
        // OnProfileSelectionChanged() (which would reload/discard the profile being edited).
        private bool _isRefreshingProfiles;

        private string? _selectedProfileName;
        public string? SelectedProfileName
        {
            get => _selectedProfileName;
            set
            {
                if (SetProperty(ref _selectedProfileName, value))
                {
                    if (!_isRefreshingProfiles)
                        OnProfileSelectionChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        // ──────────────────── Available saved paths ────────────────────

        private ObservableCollection<string> _availablePaths = new();
        public ObservableCollection<string> AvailablePaths
        {
            get => _availablePaths;
            set => SetProperty(ref _availablePaths, value);
        }

        // ──────────────────── Available custom operations ────────────────────

        /// <summary>
        /// Operation names selectable in the profile editor. Empty string means "no operation".
        /// Populated from the <see cref="BotOperations"/> registry.
        /// </summary>
        public ObservableCollection<string> AvailableOperations { get; } = new();

        private void BuildAvailableOperations()
        {
            AvailableOperations.Clear();
            AvailableOperations.Add(""); // empty = no operation
            foreach (var name in BotOperations.KnownNames)
                AvailableOperations.Add(name);
        }

        // ──────────────────── Current profile being edited ────────────────────

        private BotProfile? _currentProfile;
        public BotProfile? CurrentProfile
        {
            get => _currentProfile;
            set
            {
                if (SetProperty(ref _currentProfile, value))
                {
                    OnPropertyChanged(nameof(HasProfile));
                    OnPropertyChanged(nameof(ProfileName));
                    OnPropertyChanged(nameof(MinHpPotions));
                    OnPropertyChanged(nameof(MinManaPotions));
                    OnPropertyChanged(nameof(MaxWeightPercent));
                    OnPropertyChanged(nameof(MinHp));
                    OnPropertyChanged(nameof(MinMana));
                    OnPropertyChanged(nameof(HpBuyTarget));
                    OnPropertyChanged(nameof(ManaBuyTarget));
                    OnPropertyChanged(nameof(RedBuyTarget));
                    OnPropertyChanged(nameof(WhiteBuyTarget));
                    OnPropertyChanged(nameof(DryRunRepot));
                    OnPropertyChanged(nameof(LootPriority));
                    OnPropertyChanged(nameof(TeleportKey));
                    OnPropertyChanged(nameof(TeleportScanCode));
                    OnPropertyChanged(nameof(MaxTeleportRetries));
                    OnPropertyChanged(nameof(StartPositionCheckEnabled));
                    OnPropertyChanged(nameof(StartPositionX));
                    OnPropertyChanged(nameof(StartPositionY));
                    OnPropertyChanged(nameof(ProtectionMapNumber));
                    OnPropertyChanged(nameof(StartPositionTolerance));
                    OnPropertyChanged(nameof(WindowOffsetX));
                    OnPropertyChanged(nameof(WindowOffsetY));
                    LoadProfileIntoEditor();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool HasProfile => CurrentProfile != null;

        // ── Profile scalar fields (bound to UI) ──

        public string ProfileName
        {
            get => CurrentProfile?.Name ?? "";
            set { if (CurrentProfile != null) { CurrentProfile.Name = value; OnPropertyChanged(); } }
        }

        public int MinHpPotions
        {
            get => CurrentProfile?.MinHpPotions ?? BotConstants.Repot.DefaultMinHpPotions;
            set { if (CurrentProfile != null) { CurrentProfile.MinHpPotions = value; OnPropertyChanged(); } }
        }

        public int MinManaPotions
        {
            get => CurrentProfile?.MinManaPotions ?? BotConstants.Repot.DefaultMinManaPotions;
            set { if (CurrentProfile != null) { CurrentProfile.MinManaPotions = value; OnPropertyChanged(); } }
        }

        /// <summary>MaxWeightRatio (0..1) exposed to the UI as a percentage (0..100).</summary>
        public int MaxWeightPercent
        {
            get => (int)Math.Round((CurrentProfile?.MaxWeightRatio ?? BotConstants.Repot.DefaultMaxWeightRatio) * 100f);
            set
            {
                if (CurrentProfile != null)
                {
                    CurrentProfile.MaxWeightRatio = Math.Clamp(value, 0, 100) / 100f;
                    OnPropertyChanged();
                }
            }
        }

        public int MinHp
        {
            get => CurrentProfile?.MinHp ?? BotConstants.Repot.DefaultMinHp;
            set { if (CurrentProfile != null) { CurrentProfile.MinHp = value; OnPropertyChanged(); } }
        }

        public int MinMana
        {
            get => CurrentProfile?.MinMana ?? BotConstants.Repot.DefaultMinMana;
            set { if (CurrentProfile != null) { CurrentProfile.MinMana = value; OnPropertyChanged(); } }
        }

        public int HpBuyTarget
        {
            get => CurrentProfile?.HpBuyTarget ?? BotConstants.Repot.HpBuyTarget;
            set { if (CurrentProfile != null) { CurrentProfile.HpBuyTarget = value; OnPropertyChanged(); } }
        }

        public int ManaBuyTarget
        {
            get => CurrentProfile?.ManaBuyTarget ?? BotConstants.Repot.ManaBuyTarget;
            set { if (CurrentProfile != null) { CurrentProfile.ManaBuyTarget = value; OnPropertyChanged(); } }
        }

        public int RedBuyTarget
        {
            get => CurrentProfile?.RedBuyTarget ?? BotConstants.Repot.RedBuyTarget;
            set { if (CurrentProfile != null) { CurrentProfile.RedBuyTarget = value; OnPropertyChanged(); } }
        }

        public int WhiteBuyTarget
        {
            get => CurrentProfile?.WhiteBuyTarget ?? BotConstants.Repot.WhiteBuyTarget;
            set { if (CurrentProfile != null) { CurrentProfile.WhiteBuyTarget = value; OnPropertyChanged(); } }
        }

        public bool DryRunRepot
        {
            get => CurrentProfile?.DryRunRepot ?? false;
            set { if (CurrentProfile != null) { CurrentProfile.DryRunRepot = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Loot-priority mode: when enabled during the ExpLoop step, looting outranks
        /// combat and waypoint movement.
        /// </summary>
        public bool LootPriority
        {
            get => CurrentProfile?.LootPriority ?? false;
            set { if (CurrentProfile != null) { CurrentProfile.LootPriority = value; OnPropertyChanged(); } }
        }

        public int TeleportKey
        {
            get => CurrentProfile?.TeleportKey ?? BotConstants.Workflow.DefaultTeleportKey;
            set { if (CurrentProfile != null) { CurrentProfile.TeleportKey = value; OnPropertyChanged(); } }
        }

        public int TeleportScanCode
        {
            get => CurrentProfile?.TeleportScanCode ?? BotConstants.Workflow.DefaultTeleportScanCode;
            set { if (CurrentProfile != null) { CurrentProfile.TeleportScanCode = value; OnPropertyChanged(); } }
        }

        public int MaxTeleportRetries
        {
            get => CurrentProfile?.MaxTeleportRetries ?? BotConstants.Repot.MaxTeleportRetries;
            set { if (CurrentProfile != null) { CurrentProfile.MaxTeleportRetries = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Start-position protection: when enabled, the bot teleports to town at start
        /// if the player is not on the profile's start coordinates, then verifies the
        /// map/position against the protection settings before starting the flow.
        /// </summary>
        public bool StartPositionCheckEnabled
        {
            get => CurrentProfile?.EnableStartPositionCheck ?? false;
            set { if (CurrentProfile != null) { CurrentProfile.EnableStartPositionCheck = value; OnPropertyChanged(); } }
        }

        public int StartPositionX
        {
            get => CurrentProfile?.StartPositionX ?? 0;
            set { if (CurrentProfile != null) { CurrentProfile.StartPositionX = value; OnPropertyChanged(); } }
        }

        public int StartPositionY
        {
            get => CurrentProfile?.StartPositionY ?? 0;
            set { if (CurrentProfile != null) { CurrentProfile.StartPositionY = value; OnPropertyChanged(); } }
        }

        /// <summary>Map number the player must be on after the start teleport (0 = skip map check).</summary>
        public int ProtectionMapNumber
        {
            get => CurrentProfile?.ProtectionMapNumber ?? 0;
            set { if (CurrentProfile != null) { CurrentProfile.ProtectionMapNumber = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Tolerance in game tiles for the start-position comparison. The position memory
        /// refreshes only after the player moves, so the bot nudges the player a few
        /// steps after the teleport and accepts any position within this distance of the
        /// start coordinates.
        /// </summary>
        public int StartPositionTolerance
        {
            get => CurrentProfile?.StartPositionTolerance ?? 5;
            set { if (CurrentProfile != null) { CurrentProfile.StartPositionTolerance = value; OnPropertyChanged(); } }
        }

        private bool CanCaptureStartPosition => CurrentProfile != null && _capturePosition != null;

        /// <summary>
        /// Fills the start-position / protection values from the player's CURRENT
        /// in-game state: Start X/Y from the current position and the protected map from
        /// the current map number. When a route is selected in the Step routes panel,
        /// the values are written into THAT route's OWN start-check data; otherwise they
        /// go into the profile-level start check fields.
        /// </summary>
        private void CaptureStartPosition()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile loaded. Create or load a profile first.";
                return;
            }

            if (_capturePosition == null)
            {
                StatusText = "Game capture is not available — attach to the game first.";
                return;
            }

            var (x, y, map, success) = _capturePosition();
            if (!success)
            {
                StatusText = "Failed to read the player position / map from the game.";
                return;
            }

            // A selected route in the Step routes panel gets its OWN start-check data.
            var selectedRoute = SelectedFlowStep?.SelectedStepRoute;
            if (selectedRoute != null)
            {
                selectedRoute.StartPositionX = (int)x;
                selectedRoute.StartPositionY = (int)y;
                if (map > 0)
                {
                    selectedRoute.ProtectionMapNumber = map;
                    StatusText = $"Captured start position ({x}, {y}) and protected map {map} into route '{selectedRoute.PathFile}'.";
                }
                else
                {
                    StatusText = $"Captured start position ({x}, {y}) into route '{selectedRoute.PathFile}'; map read returned 0 — protected map left unchanged.";
                }
                return;
            }

            CurrentProfile.StartPositionX = (int)x;
            CurrentProfile.StartPositionY = (int)y;
            OnPropertyChanged(nameof(StartPositionX));
            OnPropertyChanged(nameof(StartPositionY));

            if (map > 0)
            {
                CurrentProfile.ProtectionMapNumber = map;
                OnPropertyChanged(nameof(ProtectionMapNumber));
                StatusText = $"Captured start position ({x}, {y}) and protected map {map} (profile-level start check).";
            }
            else
            {
                StatusText = $"Captured start position ({x}, {y}); map read returned 0 — protected map left unchanged (profile-level start check).";
            }
        }

        public int WindowOffsetX
        {
            get => CurrentProfile?.WindowOffsetX ?? 0;
            set { if (CurrentProfile != null) { CurrentProfile.WindowOffsetX = value; OnPropertyChanged(); } }
        }

        public int WindowOffsetY
        {
            get => CurrentProfile?.WindowOffsetY ?? 0;
            set { if (CurrentProfile != null) { CurrentProfile.WindowOffsetY = value; OnPropertyChanged(); } }
        }

        // ──────────────────── The flow of steps ────────────────────

        /// <summary>
        /// The ordered flow of steps the bot executes (Path / Repot / Operation / ExpLoop),
        /// cycled over time.
        /// </summary>
        public ObservableCollection<BotFlowStepViewModel> FlowSteps { get; } = new();

        private BotFlowStepViewModel? _selectedFlowStep;
        public BotFlowStepViewModel? SelectedFlowStep
        {
            get => _selectedFlowStep;
            set
            {
                if (SetProperty(ref _selectedFlowStep, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        // ──────────────────── Status ────────────────────

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        // ──────────────────── Commands ────────────────────

        public ICommand NewProfileCommand { get; }
        public ICommand SaveProfileCommand { get; }
        public ICommand LoadProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand RefreshProfilesCommand { get; }
        public ICommand RefreshPathsCommand { get; }
        public ICommand AddFlowStepCommand { get; }
        public ICommand RemoveFlowStepCommand { get; }
        public ICommand MoveFlowStepUpCommand { get; }
        public ICommand MoveFlowStepDownCommand { get; }
        public ICommand AddRouteCommand { get; }
        public ICommand RemoveRouteCommand { get; }
        public ICommand CaptureStartPositionCommand { get; }

        // ──────────────────── Implementation ────────────────────

        private void NewProfile()
        {
            // Clear any loaded profile selection first so it cannot discard the new profile.
            SelectedProfileName = null;

            CurrentProfile = new BotProfile
            {
                Name = "NewProfile",
                MinHpPotions = BotConstants.Repot.DefaultMinHpPotions,
                MinManaPotions = BotConstants.Repot.DefaultMinManaPotions,
                MaxWeightRatio = BotConstants.Repot.DefaultMaxWeightRatio,
                MinHp = BotConstants.Repot.DefaultMinHp,
                MinMana = BotConstants.Repot.DefaultMinMana,
                HpBuyTarget = BotConstants.Repot.HpBuyTarget,
                ManaBuyTarget = BotConstants.Repot.ManaBuyTarget,
                RedBuyTarget = BotConstants.Repot.RedBuyTarget,
                WhiteBuyTarget = BotConstants.Repot.WhiteBuyTarget,
                DryRunRepot = false,
                LootPriority = false,
                TeleportKey = BotConstants.Workflow.DefaultTeleportKey,
                TeleportScanCode = BotConstants.Workflow.DefaultTeleportScanCode,
                MaxTeleportRetries = BotConstants.Repot.MaxTeleportRetries,
                EnableStartPositionCheck = false,
                StartPositionX = 0,
                StartPositionY = 0,
                ProtectionMapNumber = 0,
                StartPositionTolerance = 5,
                WindowOffsetX = 0,
                WindowOffsetY = 0,
                FlowSteps = new List<BotFlowStep>
                {
                    new BotFlowStep { Type = BotFlowStepType.Path }
                }
            };
            StatusText = "Created new profile. Build your flow and click Save.";
        }

        private void SaveProfile()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile to save. Click 'New' first.";
                return;
            }

            if (string.IsNullOrWhiteSpace(CurrentProfile.Name))
            {
                StatusText = "Profile name cannot be empty.";
                return;
            }

            // 1. Copy editor values into the profile model
            CurrentProfile.Name = ProfileName;
            CurrentProfile.FlowSteps = FlowSteps
                .Select(BuildStepModel)
                .ToList();

            // 2. Validate the complete model (one pass, all errors reported)
            var errors = _profileLoader.ValidateProfile(CurrentProfile);

            // 3. Block saving when validation errors exist; 4. show every error in the status area
            if (errors.Count > 0)
            {
                StatusText = "Cannot save — validation errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors);
                _log("[ProfileEditor] Cannot save — validation errors:");
                foreach (var error in errors)
                    _log($"  - {error}");
                return;
            }

            // 5. Capture the profile name before any bound collection is refreshed.
            string savedName = CurrentProfile.Name;

            // 6. Save only after validation succeeds (via BotProfileLoader).
            if (!_profileLoader.SaveProfile(CurrentProfile))
            {
                // A failed write must not refresh the list or report success.
                StatusText = $"Failed to save profile '{savedName}' — check the log for details.";
                return;
            }

            // 7. Success: refresh the profile list (preserves editor state) and report success
            // using the captured name.
            RefreshProfiles();
            StatusText = $"Saved profile '{savedName}'.";
        }

        /// <summary>
        /// Copies one editor step ViewModel into a BotFlowStep model. Path and ExpLoop
        /// steps write their route group (<see cref="BotFlowStep.Routes"/>) and keep the
        /// single-route fields (PathFile / StartDelayMs) in sync with the first route so
        /// the step stays readable as a legacy single-route step.
        /// </summary>
        private static BotFlowStep BuildStepModel(BotFlowStepViewModel s)
        {
            var model = new BotFlowStep
            {
                Type = s.Type,
                PathFile = s.PathFile ?? "",
                StartDelayMs = s.StartDelayMs,
                CompletionMode = s.CompletionMode,
                ExpectedDestinationMapNumber = s.ExpectedDestinationMapNumber,
                OperationName = s.OperationName ?? "",
                RepotPaths = s.RepotPaths
                    .Select(rp => new BotRouteStep
                    {
                        PathFile = rp.PathFile,
                        StartDelayMs = rp.StartDelayMs,
                        StartCheckEnabled = rp.StartCheckEnabled,
                        StartPositionX = rp.StartPositionX,
                        StartPositionY = rp.StartPositionY,
                        ProtectionMapNumber = rp.ProtectionMapNumber,
                        StartPositionTolerance = rp.StartPositionTolerance
                    })
                    .ToList()
            };

            if (s.Type == BotFlowStepType.Path || s.Type == BotFlowStepType.ExpLoop)
            {
                model.Routes = s.Routes
                    .Select(rp => new BotRouteStep
                    {
                        PathFile = rp.PathFile,
                        StartDelayMs = rp.StartDelayMs,
                        StartCheckEnabled = rp.StartCheckEnabled,
                        StartPositionX = rp.StartPositionX,
                        StartPositionY = rp.StartPositionY,
                        ProtectionMapNumber = rp.ProtectionMapNumber,
                        StartPositionTolerance = rp.StartPositionTolerance
                    })
                    .ToList();

                if (model.Routes.Count > 0)
                {
                    model.PathFile = model.Routes[0].PathFile ?? "";
                    model.StartDelayMs = model.Routes[0].StartDelayMs;
                }
            }

            return model;
        }

        private void LoadSelectedProfile()
        {
            if (string.IsNullOrEmpty(SelectedProfileName))
                return;

            var profile = _profileLoader.LoadProfile(SelectedProfileName);
            if (profile == null)
            {
                StatusText = $"Failed to load profile '{SelectedProfileName}'.";
                RefreshProfiles();
                return;
            }

            CurrentProfile = profile;
            StatusText = $"Loaded profile '{profile.Name}' ({profile.FlowSteps?.Count ?? 0} flow steps).";
        }

        private void DeleteSelectedProfile()
        {
            if (string.IsNullOrEmpty(SelectedProfileName))
                return;

            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to delete profile '{SelectedProfileName}'?",
                "Confirm Delete",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes)
                return;

            string fileName = SelectedProfileName;
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                fileName += ".json";

            string path = Path.Combine(PROFILE_DIR, fileName);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    StatusText = $"Deleted profile '{SelectedProfileName}'.";
                }
                RefreshProfiles();
                if (CurrentProfile?.Name == SelectedProfileName)
                    CurrentProfile = null;
                SelectedProfileName = null;
            }
            catch (Exception ex)
            {
                StatusText = $"Error deleting profile: {ex.Message}";
            }
        }

        private void RefreshProfiles()
        {
            // Capture the editor state before rebuilding the list.
            string? previousSelectedName = SelectedProfileName;
            BotProfile? editedProfile = CurrentProfile;
            string? editedProfileName = editedProfile?.Name;

            _isRefreshingProfiles = true;
            try
            {
                ProfileNames.Clear();
                foreach (var name in _profileLoader.ListProfiles())
                {
                    ProfileNames.Add(name);
                }

                // Restore the selection: prefer the edited profile's name, then the previous selection.
                string? nameToSelect = null;
                if (!string.IsNullOrEmpty(editedProfileName))
                {
                    nameToSelect = ProfileNames.FirstOrDefault(n =>
                        string.Equals(n, editedProfileName, StringComparison.OrdinalIgnoreCase));
                }
                if (nameToSelect == null && !string.IsNullOrEmpty(previousSelectedName))
                {
                    nameToSelect = ProfileNames.FirstOrDefault(n =>
                        string.Equals(n, previousSelectedName, StringComparison.OrdinalIgnoreCase));
                }

                if (nameToSelect != null)
                {
                    // The profile still exists: keep the current editor state, restore the
                    // canonical spelling from the refreshed list.
                    SelectedProfileName = nameToSelect;
                }
                else if (!string.IsNullOrEmpty(previousSelectedName) || editedProfile != null)
                {
                    // Neither the edited profile nor the previous selection exists anymore.
                    SelectedProfileName = null;
                    CurrentProfile = null;
                }
            }
            finally
            {
                _isRefreshingProfiles = false;
            }

            OnPropertyChanged(nameof(SelectedProfileName));
            CommandManager.InvalidateRequerySuggested();
        }

        private void RefreshPaths()
        {
            // Capture every configured path string before clearing the list. Clearing a
            // bound ComboBox ItemsSource writes null back into the SelectedItem, so all
            // path strings and selections must be restored after the collection is rebuilt.
            var flowStepPaths = FlowSteps
                .Select(s => new
                {
                    Step = s,
                    Path = s.PathFile,
                    RepotPaths = s.RepotPaths.Select(rp => rp.PathFile).ToList(),
                    Routes = s.Routes.Select(rp => rp.PathFile).ToList()
                })
                .ToList();
            BotFlowStepViewModel? selectedStep = SelectedFlowStep;
            BotRouteStepViewModel? selectedRepotPath = SelectedFlowStep?.SelectedRepotPath;
            BotRouteStepViewModel? selectedRoute = SelectedFlowStep?.SelectedRoute;

            AvailablePaths.Clear();
            if (!Directory.Exists(PATH_DIR))
            {
                Directory.CreateDirectory(PATH_DIR);
            }

            foreach (var f in Directory.GetFiles(PATH_DIR, "*.json"))
            {
                AvailablePaths.Add(Path.GetFileName(f));
            }

            // Restore the exact stored strings, including paths that no longer exist in
            // SavedPaths, so validation can still report them.
            foreach (var captured in flowStepPaths)
            {
                captured.Step.PathFile = captured.Path;
                for (int i = 0; i < captured.RepotPaths.Count && i < captured.Step.RepotPaths.Count; i++)
                    captured.Step.RepotPaths[i].PathFile = captured.RepotPaths[i];
                for (int i = 0; i < captured.Routes.Count && i < captured.Step.Routes.Count; i++)
                    captured.Step.Routes[i].PathFile = captured.Routes[i];
            }
            if (selectedStep != null)
            {
                SelectedFlowStep = selectedStep;
                selectedStep.SelectedRepotPath = selectedRepotPath;
                selectedStep.SelectedRoute = selectedRoute;
            }
        }

        private void OnProfileSelectionChanged()
        {
            if (!string.IsNullOrEmpty(SelectedProfileName))
                LoadSelectedProfile();
            else
                CurrentProfile = null;
        }

        // ── Model → editor ViewModel mapping ──

        private void LoadProfileIntoEditor()
        {
            FlowSteps.Clear();

            if (CurrentProfile == null)
            {
                SelectedFlowStep = null;
                return;
            }

            foreach (var step in CurrentProfile.FlowSteps ?? new List<BotFlowStep>())
            {
                var vm = new BotFlowStepViewModel
                {
                    Type = step.Type,
                    PathFile = step.PathFile ?? "",
                    StartDelayMs = step.StartDelayMs,
                    CompletionMode = step.CompletionMode,
                    ExpectedDestinationMapNumber = step.ExpectedDestinationMapNumber,
                    OperationName = step.OperationName ?? ""
                };
                foreach (var rp in step.RepotPaths ?? new List<BotRouteStep>())
                {
                    vm.RepotPaths.Add(new BotRouteStepViewModel
                    {
                        PathFile = rp.PathFile ?? "",
                        StartDelayMs = rp.StartDelayMs,
                        StartCheckEnabled = rp.StartCheckEnabled,
                        StartPositionX = rp.StartPositionX,
                        StartPositionY = rp.StartPositionY,
                        ProtectionMapNumber = rp.ProtectionMapNumber,
                        StartPositionTolerance = rp.StartPositionTolerance
                    });
                }
                foreach (var route in step.Routes ?? new List<BotRouteStep>())
                {
                    vm.Routes.Add(new BotRouteStepViewModel
                    {
                        PathFile = route.PathFile ?? "",
                        StartDelayMs = route.StartDelayMs,
                        StartCheckEnabled = route.StartCheckEnabled,
                        StartPositionX = route.StartPositionX,
                        StartPositionY = route.StartPositionY,
                        ProtectionMapNumber = route.ProtectionMapNumber,
                        StartPositionTolerance = route.StartPositionTolerance
                    });
                }
                // Legacy single-route Path/ExpLoop steps (Routes empty, PathFile set):
                // surface the path as the first route so the route panel always shows it.
                if ((step.Type == BotFlowStepType.Path || step.Type == BotFlowStepType.ExpLoop) &&
                    vm.Routes.Count == 0 &&
                    !string.IsNullOrWhiteSpace(step.PathFile))
                {
                    vm.Routes.Add(new BotRouteStepViewModel
                    {
                        PathFile = step.PathFile ?? "",
                        StartDelayMs = step.StartDelayMs
                    });
                }
                FlowSteps.Add(vm);
            }
            // Loading selects the first flow step.
            SelectedFlowStep = FlowSteps.FirstOrDefault();
        }

        // ── Flow step management ──

        private void AddFlowStep()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile loaded. Create or load a profile first.";
                return;
            }

            var step = new BotFlowStepViewModel();

            // Insert after the currently selected step when possible, otherwise append.
            if (SelectedFlowStep != null)
            {
                int index = FlowSteps.IndexOf(SelectedFlowStep);
                FlowSteps.Insert(index + 1, step);
            }
            else
            {
                FlowSteps.Add(step);
            }

            SelectedFlowStep = step;
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Added flow step {FlowSteps.Count}.";
        }

        private bool CanRemoveFlowStep => SelectedFlowStep != null && FlowSteps.Count > 1;

        private void RemoveFlowStep()
        {
            if (SelectedFlowStep == null) return;
            if (FlowSteps.Count <= 1) return; // the profile must always keep at least one step

            int index = FlowSteps.IndexOf(SelectedFlowStep);
            FlowSteps.RemoveAt(index);

            // Select the nearest remaining step.
            SelectedFlowStep = FlowSteps[Math.Min(index, FlowSteps.Count - 1)];
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Removed flow step {index + 1}.";
        }

        private bool CanMoveFlowStep(int direction)
        {
            if (SelectedFlowStep == null) return false;
            int index = FlowSteps.IndexOf(SelectedFlowStep);
            int newIndex = index + direction;
            return newIndex >= 0 && newIndex < FlowSteps.Count;
        }

        private void MoveFlowStep(int direction)
        {
            if (SelectedFlowStep == null) return;

            int oldIndex = FlowSteps.IndexOf(SelectedFlowStep);
            int newIndex = oldIndex + direction;
            if (newIndex < 0 || newIndex >= FlowSteps.Count) return;

            // The item reference is preserved, so the selection stays on the same step.
            FlowSteps.Move(oldIndex, newIndex);
            CommandManager.InvalidateRequerySuggested();
        }

        // ── Route group management (Path / Repot / ExpLoop steps) ──

        private bool CanAddRoute =>
            CurrentProfile != null &&
            SelectedFlowStep is { Type: BotFlowStepType.Path or BotFlowStepType.Repot or BotFlowStepType.ExpLoop };

        private bool CanRemoveRoute =>
            SelectedFlowStep != null &&
            SelectedFlowStep.SelectedStepRoute != null &&
            SelectedFlowStep.StepRoutes.Count > 1;

        private void AddRoute()
        {
            if (SelectedFlowStep is not { Type: BotFlowStepType.Path or BotFlowStepType.Repot or BotFlowStepType.ExpLoop } step)
            {
                StatusText = "Select a Path, Repot or ExpLoop flow step first.";
                return;
            }

            var route = new BotRouteStepViewModel();
            var routes = step.StepRoutes;

            // Insert after the currently selected route when possible, otherwise append.
            if (step.SelectedStepRoute != null)
            {
                int index = routes.IndexOf(step.SelectedStepRoute);
                routes.Insert(index + 1, route);
            }
            else
            {
                routes.Add(route);
            }

            step.SelectedStepRoute = route;
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Added route {routes.Count} to flow step {FlowSteps.IndexOf(step) + 1}.";
        }

        private void RemoveRoute()
        {
            if (SelectedFlowStep is not { Type: BotFlowStepType.Path or BotFlowStepType.Repot or BotFlowStepType.ExpLoop } step) return;
            if (step.SelectedStepRoute == null) return;
            if (step.StepRoutes.Count <= 1) return; // keep at least one route

            int index = step.StepRoutes.IndexOf(step.SelectedStepRoute);
            step.StepRoutes.RemoveAt(index);

            // Select the nearest remaining route.
            step.SelectedStepRoute = step.StepRoutes[Math.Min(index, step.StepRoutes.Count - 1)];
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Removed route {index + 1} from flow step {FlowSteps.IndexOf(step) + 1}.";
        }
    }

    // ──────────────────── Helper ViewModels ────────────────────

    /// <summary>
    /// Editor representation of one flow step. Which fields are used depends on Type.
    /// </summary>
    public sealed class BotFlowStepViewModel : BaseViewModel
    {
        private BotFlowStepType _type = BotFlowStepType.Path;
        public BotFlowStepType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    OnPropertyChanged(nameof(OperationColumnVisibility));
                    OnPropertyChanged(nameof(PathOnlyColumnVisibility));
                    OnPropertyChanged(nameof(StepRoutes));
                    OnPropertyChanged(nameof(SelectedStepRoute));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>Visible for Operation steps (the Operation combo column).</summary>
        public System.Windows.Visibility OperationColumnVisibility =>
            _type == BotFlowStepType.Operation
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        /// <summary>Visible only for Path steps (Finish-when / Dest-map columns).</summary>
        public System.Windows.Visibility PathOnlyColumnVisibility =>
            _type == BotFlowStepType.Path
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        private string _pathFile = "";
        public string PathFile
        {
            get => _pathFile;
            set
            {
                if (SetProperty(ref _pathFile, value) &&
                    _type is BotFlowStepType.Path or BotFlowStepType.ExpLoop &&
                    Routes.Count > 0)
                {
                    // Keep the first route of the group in sync with the grid's single
                    // route combo so both views of the step never disagree.
                    Routes[0].PathFile = value;
                }
            }
        }

        private int _startDelayMs = 0;
        public int StartDelayMs
        {
            get => _startDelayMs;
            set => SetProperty(ref _startDelayMs, value);
        }

        private TravelRouteCompletionMode _completionMode = TravelRouteCompletionMode.FinalWaypoint;
        public TravelRouteCompletionMode CompletionMode
        {
            get => _completionMode;
            set
            {
                if (!SetProperty(ref _completionMode, value))
                    return;

                if (value == TravelRouteCompletionMode.FinalWaypoint &&
                    ExpectedDestinationMapNumber != 0)
                {
                    ExpectedDestinationMapNumber = 0;
                }
            }
        }

        private int _expectedDestinationMapNumber = 0;
        public int ExpectedDestinationMapNumber
        {
            get => _expectedDestinationMapNumber;
            set => SetProperty(ref _expectedDestinationMapNumber, value);
        }

        private string _operationName = "";
        public string OperationName
        {
            get => _operationName;
            set => SetProperty(ref _operationName, value);
        }

        /// <summary>Repot paths for a Repot step (cycled on each repot).</summary>
        public ObservableCollection<BotRouteStepViewModel> RepotPaths { get; } = new();

        private BotRouteStepViewModel? _selectedRepotPath;
        public BotRouteStepViewModel? SelectedRepotPath
        {
            get => _selectedRepotPath;
            set
            {
                if (SetProperty(ref _selectedRepotPath, value))
                {
                    OnPropertyChanged(nameof(SelectedStepRoute));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>
        /// Route group for Path and ExpLoop steps (cycled on each execution, one route
        /// per flow cycle). Mirrors how the Repot step cycles its repot paths.
        /// </summary>
        public ObservableCollection<BotRouteStepViewModel> Routes { get; } = new();

        private BotRouteStepViewModel? _selectedRoute;
        public BotRouteStepViewModel? SelectedRoute
        {
            get => _selectedRoute;
            set
            {
                if (SetProperty(ref _selectedRoute, value))
                {
                    OnPropertyChanged(nameof(SelectedStepRoute));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>
        /// The route list shown in the "Step routes" panel: <see cref="RepotPaths"/> for
        /// Repot steps, <see cref="Routes"/> for Path and ExpLoop steps.
        /// </summary>
        public ObservableCollection<BotRouteStepViewModel> StepRoutes =>
            Type == BotFlowStepType.Repot ? RepotPaths : Routes;

        /// <summary>
        /// The route selected in the "Step routes" panel: <see cref="SelectedRepotPath"/>
        /// for Repot steps, <see cref="SelectedRoute"/> for Path and ExpLoop steps.
        /// </summary>
        public BotRouteStepViewModel? SelectedStepRoute
        {
            get => Type == BotFlowStepType.Repot ? SelectedRepotPath : SelectedRoute;
            set
            {
                if (Type == BotFlowStepType.Repot)
                    SelectedRepotPath = value;
                else
                    SelectedRoute = value;
            }
        }
    }

    /// <summary>
    /// Editor representation of one route of a step's route group (Path / Repot / ExpLoop
    /// steps): a saved path reference plus startup delay.
    /// </summary>
    public class BotRouteStepViewModel : BaseViewModel
    {
        private string _pathFile = "";
        public string PathFile
        {
            get => _pathFile;
            set => SetProperty(ref _pathFile, value);
        }

        private int _startDelayMs = 0;
        public int StartDelayMs
        {
            get => _startDelayMs;
            set => SetProperty(ref _startDelayMs, value);
        }

        /// <summary>
        /// When true, the bot runs the start-position protection before executing this
        /// route using THIS route's own data (Start X / Start Y / Map / Tolerance below).
        /// </summary>
        private bool _startCheckEnabled = false;
        public bool StartCheckEnabled
        {
            get => _startCheckEnabled;
            set => SetProperty(ref _startCheckEnabled, value);
        }

        private int _startPositionX = 0;
        public int StartPositionX
        {
            get => _startPositionX;
            set => SetProperty(ref _startPositionX, value);
        }

        private int _startPositionY = 0;
        public int StartPositionY
        {
            get => _startPositionY;
            set => SetProperty(ref _startPositionY, value);
        }

        private int _protectionMapNumber = 0;
        public int ProtectionMapNumber
        {
            get => _protectionMapNumber;
            set => SetProperty(ref _protectionMapNumber, value);
        }

        private int _startPositionTolerance = 5;
        public int StartPositionTolerance
        {
            get => _startPositionTolerance;
            set => SetProperty(ref _startPositionTolerance, value);
        }
    }
}