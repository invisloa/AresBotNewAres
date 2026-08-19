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

            AddFlowStepCommand = new RelayCommand(_ => AddFlowStep(), _ => CurrentProfile != null);
            RemoveFlowStepCommand = new RelayCommand(_ => RemoveFlowStep(), _ => CanRemoveFlowStep);
            MoveFlowStepUpCommand = new RelayCommand(_ => MoveFlowStep(-1), _ => CanMoveFlowStep(-1));
            MoveFlowStepDownCommand = new RelayCommand(_ => MoveFlowStep(1), _ => CanMoveFlowStep(1));

            AddRepotPathCommand = new RelayCommand(_ => AddRepotPath(), _ => CanAddRepotPath);
            RemoveRepotPathCommand = new RelayCommand(_ => RemoveRepotPath(), _ => CanRemoveRepotPath);

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
        public ICommand AddRepotPathCommand { get; }
        public ICommand RemoveRepotPathCommand { get; }

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
                .Select(s => new BotFlowStep
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
                            StartDelayMs = rp.StartDelayMs
                        })
                        .ToList()
                })
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
                    RepotPaths = s.RepotPaths.Select(rp => rp.PathFile).ToList()
                })
                .ToList();
            BotFlowStepViewModel? selectedStep = SelectedFlowStep;
            BotRouteStepViewModel? selectedRepotPath = SelectedFlowStep?.SelectedRepotPath;

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
            }
            if (selectedStep != null)
            {
                SelectedFlowStep = selectedStep;
                selectedStep.SelectedRepotPath = selectedRepotPath;
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
                        StartDelayMs = rp.StartDelayMs
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

        // ── Repot step: repot path management ──

        private bool CanAddRepotPath =>
            CurrentProfile != null && SelectedFlowStep is { Type: BotFlowStepType.Repot };

        private bool CanRemoveRepotPath =>
            SelectedFlowStep is { Type: BotFlowStepType.Repot } s &&
            s.SelectedRepotPath != null &&
            s.RepotPaths.Count > 1;

        private void AddRepotPath()
        {
            if (SelectedFlowStep is not { Type: BotFlowStepType.Repot } repotStep)
            {
                StatusText = "Select a Repot flow step first.";
                return;
            }

            var repotPath = new BotRouteStepViewModel();

            // Insert after the currently selected repot path when possible, otherwise append.
            if (repotStep.SelectedRepotPath != null)
            {
                int index = repotStep.RepotPaths.IndexOf(repotStep.SelectedRepotPath);
                repotStep.RepotPaths.Insert(index + 1, repotPath);
            }
            else
            {
                repotStep.RepotPaths.Add(repotPath);
            }

            repotStep.SelectedRepotPath = repotPath;
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Added repot path {repotStep.RepotPaths.Count}.";
        }

        private void RemoveRepotPath()
        {
            if (SelectedFlowStep is not { Type: BotFlowStepType.Repot } repotStep) return;
            if (repotStep.SelectedRepotPath == null) return;
            if (repotStep.RepotPaths.Count <= 1) return; // keep at least one repot path

            int index = repotStep.RepotPaths.IndexOf(repotStep.SelectedRepotPath);
            repotStep.RepotPaths.RemoveAt(index);

            // Select the nearest remaining path.
            repotStep.SelectedRepotPath = repotStep.RepotPaths[Math.Min(index, repotStep.RepotPaths.Count - 1)];
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Removed repot path {index + 1}.";
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
                    OnPropertyChanged(nameof(PathColumnVisibility));
                    OnPropertyChanged(nameof(OperationColumnVisibility));
                    OnPropertyChanged(nameof(PathOnlyColumnVisibility));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>Visible for Path and ExpLoop steps (the Path combo column).</summary>
        public System.Windows.Visibility PathColumnVisibility =>
            (_type == BotFlowStepType.Path || _type == BotFlowStepType.ExpLoop)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

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
                    CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// Editor representation of one repot path: a saved path reference plus startup delay.
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
}