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
    /// A profile is a simple three-stage configuration:
    ///   1. REPOT      — CityToRepotPaths (list of paths, cycled on every repot)
    ///   2. GO TO EXP  — TravelToExpRoutes (ordered list of paths)
    ///   3. EXP PATH   — ExpLoop (one looping path)
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

        public ProfileEditorViewModel(Action<string>? log = null)
        {
            _log = log ?? (_ => { });
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

            AddTravelRouteCommand = new RelayCommand(_ => AddTravelRoute(), _ => CurrentProfile != null);
            RemoveTravelRouteCommand = new RelayCommand(_ => RemoveTravelRoute(), _ => CanRemoveTravelRoute);
            MoveTravelRouteUpCommand = new RelayCommand(_ => MoveTravelRoute(-1), _ => CanMoveTravelRoute(-1));
            MoveTravelRouteDownCommand = new RelayCommand(_ => MoveTravelRoute(1), _ => CanMoveTravelRoute(1));

            AddRepotPathCommand = new RelayCommand(_ => AddRepotPath(), _ => CurrentProfile != null);
            RemoveRepotPathCommand = new RelayCommand(_ => RemoveRepotPath(), _ => CanRemoveRepotPath);
            MoveRepotPathUpCommand = new RelayCommand(_ => MoveRepotPath(-1), _ => CanMoveRepotPath(-1));
            MoveRepotPathDownCommand = new RelayCommand(_ => MoveRepotPath(1), _ => CanMoveRepotPath(1));

            AddPreExpOperationCommand = new RelayCommand(_ => AddPreExpOperation(), _ => CurrentProfile != null);
            RemovePreExpOperationCommand = new RelayCommand(_ => RemovePreExpOperation(), _ => CanRemovePreExpOperation);

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
        /// Loot-priority mode: when enabled, looting outranks combat and waypoint
        /// movement — the bot scans for loot even while attacking a mob and suspends
        /// attack/movement while it walks to loot.
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

        // ──────────────────── Stage 1: REPOT ────────────────────

        /// <summary>
        /// Ordered list of paths from the city/player starting position to the repot
        /// location. The bot uses the next path in the list on every repot trip
        /// (cycling back to the first after the last).
        /// </summary>
        public ObservableCollection<BotRouteStepViewModel> CityToRepotPaths { get; } = new();

        private BotRouteStepViewModel? _selectedRepotPath;
        public BotRouteStepViewModel? SelectedRepotPath
        {
            get => _selectedRepotPath;
            set
            {
                if (SetProperty(ref _selectedRepotPath, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        // ──────────────────── Stage 2: GO TO EXP ────────────────────

        /// <summary>Ordered list of paths from the repot location to the EXP position.</summary>
        public ObservableCollection<TravelRouteStepViewModel> TravelToExpRoutes { get; } = new();

        private TravelRouteStepViewModel? _selectedTravelRoute;
        public TravelRouteStepViewModel? SelectedTravelRoute
        {
            get => _selectedTravelRoute;
            set
            {
                if (SetProperty(ref _selectedTravelRoute, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

// ──────────────────── Stage 3: EXP PATH ────────────────────

        public BotRouteStepViewModel ExpLoop { get; } = new();

        // ──────────────────── Custom operations (pre-EXP) ────────────────────

        /// <summary>Ordered list of operation names to run at the EXP map before hunting starts.</summary>
        public ObservableCollection<string> PreExpOperations { get; } = new();

        private string? _selectedPreExpOperation;
        public string? SelectedPreExpOperation
        {
            get => _selectedPreExpOperation;
            set
            {
                if (SetProperty(ref _selectedPreExpOperation, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Operation name chosen in the "Add operation" combo box.</summary>
        private string? _newPreExpOperation = "";
        public string? NewPreExpOperation
        {
            get => _newPreExpOperation;
            set => SetProperty(ref _newPreExpOperation, value);
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
        public ICommand AddTravelRouteCommand { get; }
        public ICommand RemoveTravelRouteCommand { get; }
        public ICommand MoveTravelRouteUpCommand { get; }
        public ICommand MoveTravelRouteDownCommand { get; }
        public ICommand AddRepotPathCommand { get; }
        public ICommand RemoveRepotPathCommand { get; }
        public ICommand MoveRepotPathUpCommand { get; }
        public ICommand MoveRepotPathDownCommand { get; }
        public ICommand AddPreExpOperationCommand { get; }
        public ICommand RemovePreExpOperationCommand { get; }

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
                WindowOffsetX = 0,
                WindowOffsetY = 0,
                CityToRepotPaths = new List<BotRouteStep> { new BotRouteStep() },
                TravelToExpRoutes = new List<TravelRouteStep>
                {
                    new TravelRouteStep
                    {
                        CompletionMode = TravelRouteCompletionMode.FinalWaypoint,
                        StartDelayMs = 0,
                        ExpectedDestinationMapNumber = 0
                    }
                },
                ExpLoop = new BotRouteStep(),
                PreExpOperations = new List<string>()
            };
            StatusText = "Created new profile. Fill in the three stages and click Save.";
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
            CurrentProfile.CityToRepotPaths = CityToRepotPaths
                .Select(r => new BotRouteStep
                {
                    PathFile = r.PathFile,
                    StartDelayMs = r.StartDelayMs
                })
                .ToList();
            CurrentProfile.TravelToExpRoutes = TravelToExpRoutes
                .Select(r => new TravelRouteStep
                {
                    PathFile = r.PathFile,
                    StartDelayMs = r.StartDelayMs,
                    CompletionMode = r.CompletionMode,
                    ExpectedDestinationMapNumber = r.ExpectedDestinationMapNumber,
                    OperationBefore = r.OperationBefore,
                    OperationAfter = r.OperationAfter
                })
                .ToList();
            CurrentProfile.ExpLoop ??= new BotRouteStep();
            CurrentProfile.ExpLoop.PathFile = ExpLoop.PathFile;
            CurrentProfile.ExpLoop.StartDelayMs = ExpLoop.StartDelayMs;
            CurrentProfile.PreExpOperations = PreExpOperations
                .Where(op => !string.IsNullOrWhiteSpace(op))
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
            StatusText = $"Loaded profile '{profile.Name}'.";
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
            // Capture every configured path and the route selection before clearing the list.
            // Clearing a bound ComboBox ItemsSource writes null back into the SelectedItem,
            // so the paths and the selection must be restored after the collection is repopulated.
            BotRouteStepViewModel? selectedRepotPath = SelectedRepotPath;
            var repotPaths = CityToRepotPaths.Select(p => p.PathFile).ToList();
            TravelRouteStepViewModel? selectedRoute = SelectedTravelRoute;
            var routePaths = TravelToExpRoutes.Select(r => r.PathFile).ToList();
            string expLoopPath = ExpLoop.PathFile;

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
            for (int i = 0; i < repotPaths.Count && i < CityToRepotPaths.Count; i++)
                CityToRepotPaths[i].PathFile = repotPaths[i];
            for (int i = 0; i < routePaths.Count && i < TravelToExpRoutes.Count; i++)
                TravelToExpRoutes[i].PathFile = routePaths[i];
            ExpLoop.PathFile = expLoopPath;
            SelectedRepotPath = selectedRepotPath;
            SelectedTravelRoute = selectedRoute;
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
            CityToRepotPaths.Clear();
            TravelToExpRoutes.Clear();

            if (CurrentProfile == null)
            {
                ExpLoop.PathFile = "";
                ExpLoop.StartDelayMs = 0;
                SelectedRepotPath = null;
                SelectedTravelRoute = null;
                return;
            }

            foreach (var step in CurrentProfile.CityToRepotPaths ?? new List<BotRouteStep>())
            {
                CityToRepotPaths.Add(new BotRouteStepViewModel
                {
                    PathFile = step.PathFile ?? "",
                    StartDelayMs = step.StartDelayMs
                });
            }
            // Loading selects the first repot path.
            SelectedRepotPath = CityToRepotPaths.FirstOrDefault();

            foreach (var r in CurrentProfile.TravelToExpRoutes ?? new List<TravelRouteStep>())
            {
                TravelToExpRoutes.Add(new TravelRouteStepViewModel
                {
                    PathFile = r.PathFile ?? "",
                    StartDelayMs = r.StartDelayMs,
                    CompletionMode = r.CompletionMode,
                    ExpectedDestinationMapNumber = r.ExpectedDestinationMapNumber,
                    OperationBefore = r.OperationBefore ?? "",
                    OperationAfter = r.OperationAfter ?? ""
                });
            }
            // Loading selects the first Go to EXP path.
            SelectedTravelRoute = TravelToExpRoutes.FirstOrDefault();

            PreExpOperations.Clear();
            foreach (var op in CurrentProfile.PreExpOperations ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(op))
                    PreExpOperations.Add(op);
            }
            SelectedPreExpOperation = PreExpOperations.FirstOrDefault();

            ExpLoop.PathFile = CurrentProfile.ExpLoop?.PathFile ?? "";
            ExpLoop.StartDelayMs = CurrentProfile.ExpLoop?.StartDelayMs ?? 0;
        }

        // ── Stage 1: Repot path management ──

        private void AddRepotPath()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile loaded. Create or load a profile first.";
                return;
            }

            var step = new BotRouteStepViewModel();

            // Insert after the currently selected path when possible, otherwise append.
            if (SelectedRepotPath != null)
            {
                int index = CityToRepotPaths.IndexOf(SelectedRepotPath);
                CityToRepotPaths.Insert(index + 1, step);
            }
            else
            {
                CityToRepotPaths.Add(step);
            }

            SelectedRepotPath = step;
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Added repot path {CityToRepotPaths.Count}.";
        }

        private bool CanRemoveRepotPath =>
            SelectedRepotPath != null && CityToRepotPaths.Count > 1;

        private void RemoveRepotPath()
        {
            if (SelectedRepotPath == null) return;
            if (CityToRepotPaths.Count <= 1) return; // the profile must always keep at least one repot path

            int index = CityToRepotPaths.IndexOf(SelectedRepotPath);
            CityToRepotPaths.RemoveAt(index);

            // Select the nearest remaining path.
            SelectedRepotPath = CityToRepotPaths[Math.Min(index, CityToRepotPaths.Count - 1)];
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Removed repot path {index + 1}.";
        }

        private bool CanMoveRepotPath(int direction)
        {
            if (SelectedRepotPath == null) return false;
            int index = CityToRepotPaths.IndexOf(SelectedRepotPath);
            int newIndex = index + direction;
            return newIndex >= 0 && newIndex < CityToRepotPaths.Count;
        }

        private void MoveRepotPath(int direction)
        {
            if (SelectedRepotPath == null) return;

            int oldIndex = CityToRepotPaths.IndexOf(SelectedRepotPath);
            int newIndex = oldIndex + direction;
            if (newIndex < 0 || newIndex >= CityToRepotPaths.Count) return;

            // The item reference is preserved, so the selection stays on the same path.
            CityToRepotPaths.Move(oldIndex, newIndex);
            CommandManager.InvalidateRequerySuggested();
        }

        // ── Custom operations (pre-EXP) management ──

        private void AddPreExpOperation()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile loaded. Create or load a profile first.";
                return;
            }

            string op = NewPreExpOperation?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(op))
            {
                StatusText = "Select an operation name first.";
                return;
            }

            if (PreExpOperations.Contains(op))
            {
                StatusText = $"Operation '{op}' is already in the list.";
                return;
            }

            PreExpOperations.Add(op);
            SelectedPreExpOperation = op;
            NewPreExpOperation = "";
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Added pre-EXP operation '{op}'.";
        }

        private bool CanRemovePreExpOperation => SelectedPreExpOperation != null;

        private void RemovePreExpOperation()
        {
            if (SelectedPreExpOperation == null) return;

            string op = SelectedPreExpOperation;
            PreExpOperations.Remove(op);

            SelectedPreExpOperation = PreExpOperations.FirstOrDefault();
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Removed pre-EXP operation '{op}'.";
        }

        // ── Stage 2: Go to EXP path management ──

        private void AddTravelRoute()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile loaded. Create or load a profile first.";
                return;
            }

            var route = new TravelRouteStepViewModel
            {
                CompletionMode = TravelRouteCompletionMode.FinalWaypoint,
                StartDelayMs = 0,
                ExpectedDestinationMapNumber = 0
            };

            // Insert after the currently selected path when possible, otherwise append.
            if (SelectedTravelRoute != null)
            {
                int index = TravelToExpRoutes.IndexOf(SelectedTravelRoute);
                TravelToExpRoutes.Insert(index + 1, route);
            }
            else
            {
                TravelToExpRoutes.Add(route);
            }

            SelectedTravelRoute = route;
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Added Go to EXP path {TravelToExpRoutes.Count}.";
        }

        private bool CanRemoveTravelRoute =>
            SelectedTravelRoute != null && TravelToExpRoutes.Count > 1;

        private void RemoveTravelRoute()
        {
            if (SelectedTravelRoute == null) return;
            if (TravelToExpRoutes.Count <= 1) return; // the profile must always keep at least one path

            int index = TravelToExpRoutes.IndexOf(SelectedTravelRoute);
            TravelToExpRoutes.RemoveAt(index);

            // Select the nearest remaining path.
            SelectedTravelRoute = TravelToExpRoutes[Math.Min(index, TravelToExpRoutes.Count - 1)];
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Removed Go to EXP path {index + 1}.";
        }

        private bool CanMoveTravelRoute(int direction)
        {
            if (SelectedTravelRoute == null) return false;
            int index = TravelToExpRoutes.IndexOf(SelectedTravelRoute);
            int newIndex = index + direction;
            return newIndex >= 0 && newIndex < TravelToExpRoutes.Count;
        }

        private void MoveTravelRoute(int direction)
        {
            if (SelectedTravelRoute == null) return;

            int oldIndex = TravelToExpRoutes.IndexOf(SelectedTravelRoute);
            int newIndex = oldIndex + direction;
            if (newIndex < 0 || newIndex >= TravelToExpRoutes.Count) return;

            // The item reference is preserved, so the selection stays on the same path.
            TravelToExpRoutes.Move(oldIndex, newIndex);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ──────────────────── Helper ViewModels ────────────────────

    /// <summary>
    /// Editor representation of one path step: a saved path reference
    /// plus the startup delay in milliseconds.
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
    }

    /// <summary>
    /// Editor representation of one Go to EXP path: a saved path reference,
    /// a startup delay, the completion mode and the expected destination map.
    /// </summary>
    public sealed class TravelRouteStepViewModel : BaseViewModel
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

        private string _operationBefore = "";
        public string OperationBefore
        {
            get => _operationBefore;
            set => SetProperty(ref _operationBefore, value);
        }

        private string _operationAfter = "";
        public string OperationAfter
        {
            get => _operationAfter;
            set => SetProperty(ref _operationAfter, value);
        }
    }
}
