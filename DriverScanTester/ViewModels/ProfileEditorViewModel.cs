using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.Utils;

namespace DriverScanTester.ViewModels
{
    /// <summary>
    /// ViewModel for the Profile Editor tab in PathEditorWindow.
    /// Allows creating/editing BotProfiles with the four-stage route workflow:
    /// Stage 1 (City → Repot, profile-level) and per-hunt stages 2-4
    /// (Repot → Outside City, Outside City → Exp Spot, Exp Loop).
    /// Uses BotProfileLoader as the single persistence and validation service.
    /// </summary>
    public class ProfileEditorViewModel : BaseViewModel
    {
        // Used only for deleting profile files (persistence itself lives in BotProfileLoader).
        private static readonly string PROFILE_DIR = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedBotProfiles"));
        private static readonly string SEGMENT_DIR = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));

        private readonly Action<string> _log;
        private readonly BotProfileLoader _profileLoader;

        public ProfileEditorViewModel(Action<string>? log = null)
        {
            _log = log ?? (_ => { });
            _profileLoader = new BotProfileLoader(_log);

            if (!Directory.Exists(SEGMENT_DIR))
                Directory.CreateDirectory(SEGMENT_DIR);

            // Commands
            NewProfileCommand = new RelayCommand(_ => NewProfile());
            SaveProfileCommand = new RelayCommand(_ => SaveProfile());
            LoadProfileCommand = new RelayCommand(_ => LoadSelectedProfile(), _ => SelectedProfileName != null);
            DeleteProfileCommand = new RelayCommand(_ => DeleteSelectedProfile(), _ => SelectedProfileName != null);
            RefreshProfilesCommand = new RelayCommand(_ => RefreshProfiles());
            RefreshSegmentsCommand = new RelayCommand(_ => RefreshSegments());

            AddHuntCommand = new RelayCommand(_ => AddHunt(), _ => CurrentProfile != null);
            RemoveHuntCommand = new RelayCommand(_ => RemoveHunt(), _ => SelectedHunt != null);
            MoveHuntUpCommand = new RelayCommand(_ => MoveHunt(-1), _ => SelectedHunt != null);
            MoveHuntDownCommand = new RelayCommand(_ => MoveHunt(1), _ => SelectedHunt != null);
            SetDefaultHuntCommand = new RelayCommand(_ => SetDefaultHunt(), _ => SelectedHunt != null);

            // Initial loads
            RefreshProfiles();
            RefreshSegments();
        }

        // ──────────────────── Profile list ────────────────────

        private ObservableCollection<string> _profileNames = new();
        public ObservableCollection<string> ProfileNames
        {
            get => _profileNames;
            set => SetProperty(ref _profileNames, value);
        }

        private string? _selectedProfileName;
        public string? SelectedProfileName
        {
            get => _selectedProfileName;
            set
            {
                if (SetProperty(ref _selectedProfileName, value))
                {
                    OnProfileSelectionChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        // ──────────────────── Available segments ────────────────────

        private ObservableCollection<string> _availableSegments = new();
        public ObservableCollection<string> AvailableSegments
        {
            get => _availableSegments;
            set => SetProperty(ref _availableSegments, value);
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
                    OnPropertyChanged(nameof(MaxWeightRatio));
                    OnPropertyChanged(nameof(MinHp));
                    OnPropertyChanged(nameof(MinMana));
                    OnPropertyChanged(nameof(HpBuyTarget));
                    OnPropertyChanged(nameof(ManaBuyTarget));
                    OnPropertyChanged(nameof(RedBuyTarget));
                    OnPropertyChanged(nameof(WhiteBuyTarget));
                    OnPropertyChanged(nameof(DryRunRepot));
                    OnPropertyChanged(nameof(TeleportKey));
                    OnPropertyChanged(nameof(TeleportScanCode));
                    OnPropertyChanged(nameof(MaxTeleportRetries));
                    OnPropertyChanged(nameof(WindowOffsetX));
                    OnPropertyChanged(nameof(WindowOffsetY));
                    OnPropertyChanged(nameof(DefaultHuntName));
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

        public float MaxWeightRatio
        {
            get => CurrentProfile?.MaxWeightRatio ?? BotConstants.Repot.DefaultMaxWeightRatio;
            set { if (CurrentProfile != null) { CurrentProfile.MaxWeightRatio = value; OnPropertyChanged(); } }
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

        public string DefaultHuntName
        {
            get => CurrentProfile?.DefaultHuntName ?? "";
            set { if (CurrentProfile != null) { CurrentProfile.DefaultHuntName = value; OnPropertyChanged(); RefreshDefaultMarkers(); } }
        }

        // ──────────────────── Stage 1: City → Repot (profile-level) ────────────────────

        public BotRouteStepViewModel CityToRepot { get; } = new();

        // ──────────────────── Hunt list ────────────────────

        private ObservableCollection<HuntDefinitionViewModel> _huntDefinitions = new();
        public ObservableCollection<HuntDefinitionViewModel> HuntDefinitions
        {
            get => _huntDefinitions;
            set => SetProperty(ref _huntDefinitions, value);
        }

        private HuntDefinitionViewModel? _selectedHunt;
        public HuntDefinitionViewModel? SelectedHunt
        {
            get => _selectedHunt;
            set
            {
                if (SetProperty(ref _selectedHunt, value))
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
        public ICommand RefreshSegmentsCommand { get; }
        public ICommand AddHuntCommand { get; }
        public ICommand RemoveHuntCommand { get; }
        public ICommand MoveHuntUpCommand { get; }
        public ICommand MoveHuntDownCommand { get; }
        public ICommand SetDefaultHuntCommand { get; }

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
                TeleportKey = BotConstants.Workflow.DefaultTeleportKey,
                TeleportScanCode = BotConstants.Workflow.DefaultTeleportScanCode,
                MaxTeleportRetries = BotConstants.Repot.MaxTeleportRetries,
                WindowOffsetX = 0,
                WindowOffsetY = 0,
                CityToRepot = new BotRouteStep(),
                HuntDefinitions = new List<HuntDefinition> { new HuntDefinition { Name = "Default" } },
                DefaultHuntName = "Default"
            };
            StatusText = "Created new profile with hunt 'Default'. Fill in the fields and click Save.";
        }

        private void SaveProfile()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile to save. Click 'New Profile' first.";
                return;
            }

            if (string.IsNullOrWhiteSpace(CurrentProfile.Name))
            {
                StatusText = "Profile Name cannot be empty.";
                return;
            }

            // 1. Copy editor values into the profile model
            CurrentProfile.Name = ProfileName;
            CurrentProfile.CityToRepot ??= new BotRouteStep();
            CurrentProfile.CityToRepot.PathFile = CityToRepot.PathFile;
            CurrentProfile.CityToRepot.StartDelayMs = CityToRepot.StartDelayMs;
            CurrentProfile.HuntDefinitions = HuntDefinitions
                .Select(h => new HuntDefinition
                {
                    Name = h.Name,
                    RepotToCityExit = new BotRouteStep
                    {
                        PathFile = h.RepotToCityExit.PathFile,
                        StartDelayMs = h.RepotToCityExit.StartDelayMs
                    },
                    CityExitToExp = new BotRouteStep
                    {
                        PathFile = h.CityExitToExp.PathFile,
                        StartDelayMs = h.CityExitToExp.StartDelayMs
                    },
                    ExpLoop = new BotRouteStep
                    {
                        PathFile = h.ExpLoop.PathFile,
                        StartDelayMs = h.ExpLoop.StartDelayMs
                    }
                })
                .ToList();
            CurrentProfile.DefaultHuntName = DefaultHuntName;

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

            // 5. Save only after validation succeeds (via BotProfileLoader)
            _profileLoader.SaveProfile(CurrentProfile);
            RefreshProfiles();
            StatusText = $"Saved profile '{CurrentProfile.Name}'.";
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
            StatusText = $"Loaded profile '{profile.Name}' ({profile.HuntDefinitions?.Count ?? 0} hunt(s)).";
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
            ProfileNames.Clear();
            foreach (var name in _profileLoader.ListProfiles())
            {
                ProfileNames.Add(name);
            }
        }

        private void RefreshSegments()
        {
            AvailableSegments.Clear();
            if (!Directory.Exists(SEGMENT_DIR))
            {
                Directory.CreateDirectory(SEGMENT_DIR);
                return;
            }

            foreach (var f in Directory.GetFiles(SEGMENT_DIR, "*.json"))
            {
                AvailableSegments.Add(Path.GetFileName(f));
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
            DetachAllHunts();
            HuntDefinitions.Clear();

            if (CurrentProfile == null)
            {
                CityToRepot.PathFile = "";
                CityToRepot.StartDelayMs = 0;
                SelectedHunt = null;
                return;
            }

            CityToRepot.PathFile = CurrentProfile.CityToRepot?.PathFile ?? "";
            CityToRepot.StartDelayMs = CurrentProfile.CityToRepot?.StartDelayMs ?? 0;

            foreach (var h in CurrentProfile.HuntDefinitions ?? new List<HuntDefinition>())
            {
                var vm = new HuntDefinitionViewModel { Name = h.Name };
                vm.RepotToCityExit.PathFile = h.RepotToCityExit?.PathFile ?? "";
                vm.RepotToCityExit.StartDelayMs = h.RepotToCityExit?.StartDelayMs ?? 0;
                vm.CityExitToExp.PathFile = h.CityExitToExp?.PathFile ?? "";
                vm.CityExitToExp.StartDelayMs = h.CityExitToExp?.StartDelayMs ?? 0;
                vm.ExpLoop.PathFile = h.ExpLoop?.PathFile ?? "";
                vm.ExpLoop.StartDelayMs = h.ExpLoop?.StartDelayMs ?? 0;
                AttachHunt(vm);
                HuntDefinitions.Add(vm);
            }

            RefreshDefaultMarkers();

            // Select the default hunt, or the first one when no valid default is configured.
            var defaultHunt = HuntDefinitions.FirstOrDefault(h =>
                string.Equals(h.Name, CurrentProfile.DefaultHuntName, StringComparison.OrdinalIgnoreCase));
            SelectedHunt = defaultHunt ?? HuntDefinitions.FirstOrDefault();
        }

        // ── Hunt management ──

        private void AddHunt()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile loaded. Create or load a profile first.";
                return;
            }

            string baseName = "New Hunt";
            string huntName = baseName;
            var existingNames = new HashSet<string>(HuntDefinitions.Select(h => h.Name), StringComparer.OrdinalIgnoreCase);
            int counter = 2;
            while (existingNames.Contains(huntName))
                huntName = $"{baseName} {counter++}";

            var hunt = new HuntDefinitionViewModel { Name = huntName };
            AttachHunt(hunt);
            HuntDefinitions.Add(hunt);
            SelectedHunt = hunt;
            RefreshDefaultMarkers();
            StatusText = $"Added hunt '{huntName}'.";
        }

        private void RemoveHunt()
        {
            if (SelectedHunt == null) return;

            string name = SelectedHunt.Name;
            int index = HuntDefinitions.IndexOf(SelectedHunt);
            var removed = SelectedHunt;
            HuntDefinitions.Remove(removed);
            DetachHunt(removed);

            // If the removed hunt was the default, update DefaultHuntName accordingly.
            if (CurrentProfile != null &&
                string.Equals(CurrentProfile.DefaultHuntName, name, StringComparison.OrdinalIgnoreCase))
            {
                CurrentProfile.DefaultHuntName = HuntDefinitions.Count > 0 ? HuntDefinitions[0].Name : "";
                OnPropertyChanged(nameof(DefaultHuntName));
            }

            if (HuntDefinitions.Count > 0)
                SelectedHunt = HuntDefinitions[Math.Min(index, HuntDefinitions.Count - 1)];
            else
                SelectedHunt = null;

            RefreshDefaultMarkers();
            StatusText = $"Removed hunt '{name}'.";
        }

        private void MoveHunt(int direction)
        {
            if (SelectedHunt == null) return;
            int oldIndex = HuntDefinitions.IndexOf(SelectedHunt);
            int newIndex = oldIndex + direction;
            if (newIndex >= 0 && newIndex < HuntDefinitions.Count)
            {
                HuntDefinitions.Move(oldIndex, newIndex);
            }
        }

        private void SetDefaultHunt()
        {
            if (CurrentProfile == null || SelectedHunt == null) return;

            CurrentProfile.DefaultHuntName = SelectedHunt.Name;
            OnPropertyChanged(nameof(DefaultHuntName));
            RefreshDefaultMarkers();
            StatusText = $"'{SelectedHunt.Name}' is now the default hunt.";
        }

        private void RefreshDefaultMarkers()
        {
            string defaultName = CurrentProfile?.DefaultHuntName ?? "";
            foreach (var h in HuntDefinitions)
                h.IsDefault = string.Equals(h.Name, defaultName, StringComparison.OrdinalIgnoreCase);
        }

        // ── Hunt rename sync (keep DefaultHuntName pointing at the default hunt) ──

        private void AttachHunt(HuntDefinitionViewModel hunt) => hunt.PropertyChanged += OnHuntPropertyChanged;

        private void DetachHunt(HuntDefinitionViewModel hunt) => hunt.PropertyChanged -= OnHuntPropertyChanged;

        private void DetachAllHunts()
        {
            foreach (var h in HuntDefinitions)
                DetachHunt(h);
        }

        private void OnHuntPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(HuntDefinitionViewModel.Name)) return;
            if (sender is not HuntDefinitionViewModel hunt) return;
            if (CurrentProfile == null) return;

            // When the default hunt is renamed, follow it so the relationship does not break.
            if (string.Equals(CurrentProfile.DefaultHuntName, hunt.PreviousName, StringComparison.OrdinalIgnoreCase))
            {
                CurrentProfile.DefaultHuntName = hunt.Name;
                OnPropertyChanged(nameof(DefaultHuntName));
                RefreshDefaultMarkers();
            }
        }
    }

    // ──────────────────── Helper ViewModels ────────────────────

    /// <summary>
    /// Editor representation of one route step: a saved path segment reference
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

    public class HuntDefinitionViewModel : BaseViewModel
    {
        private string _name = "";
        private string _previousName = "";

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _previousName = _name;
                _name = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Name before the last rename (used to keep DefaultHuntName in sync).</summary>
        public string PreviousName => _previousName;

        public BotRouteStepViewModel RepotToCityExit { get; } = new();
        public BotRouteStepViewModel CityExitToExp { get; } = new();
        public BotRouteStepViewModel ExpLoop { get; } = new();

        private bool _isDefault;
        /// <summary>True when this hunt is currently the profile's default hunt.</summary>
        public bool IsDefault
        {
            get => _isDefault;
            set => SetProperty(ref _isDefault, value);
        }
    }
}
