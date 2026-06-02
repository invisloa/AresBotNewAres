using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.Utils;

namespace DriverScanTester.ViewModels
{
    /// <summary>
    /// ViewModel for the Profile Editor tab in PathEditorWindow.
    /// Allows creating/editing BotProfiles and their HuntDefinitions/StartRoutes
    /// by referencing existing path segments from SavedPaths/.
    /// </summary>
    public class ProfileEditorViewModel : BaseViewModel
    {
        private static readonly string PROFILE_DIR = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedBotProfiles"));
        private static readonly string SEGMENT_DIR = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));

        private readonly Action<string> _log;

        public ProfileEditorViewModel(Action<string>? log = null)
        {
            _log = log ?? (_ => { });

            if (!Directory.Exists(PROFILE_DIR))
                Directory.CreateDirectory(PROFILE_DIR);
            if (!Directory.Exists(SEGMENT_DIR))
                Directory.CreateDirectory(SEGMENT_DIR);

            // Commands
            NewProfileCommand = new RelayCommand(_ => NewProfile());
            SaveProfileCommand = new RelayCommand(_ => SaveProfile());
            LoadProfileCommand = new RelayCommand(_ => LoadSelectedProfile(), _ => SelectedProfileName != null);
            DeleteProfileCommand = new RelayCommand(_ => DeleteSelectedProfile(), _ => SelectedProfileName != null);
            RefreshProfilesCommand = new RelayCommand(_ => RefreshProfiles());
            RefreshSegmentsCommand = new RelayCommand(_ => RefreshSegments());

            AddHuntCommand = new RelayCommand(_ => AddHunt());
            RemoveHuntCommand = new RelayCommand(_ => RemoveHunt(), _ => SelectedHunt != null);
            MoveHuntUpCommand = new RelayCommand(_ => MoveHunt(-1), _ => SelectedHunt != null);
            MoveHuntDownCommand = new RelayCommand(_ => MoveHunt(1), _ => SelectedHunt != null);

            AddStartRouteCommand = new RelayCommand(_ => AddStartRoute());
            RemoveStartRouteCommand = new RelayCommand(_ => RemoveStartRoute(), _ => SelectedStartRoute != null);

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

        private string? _selectedRepotToExpSegment;
        public string? SelectedRepotToExpSegment
        {
            get => _selectedRepotToExpSegment;
            set => SetProperty(ref _selectedRepotToExpSegment, value);
        }

        private string? _selectedExpLoopSegment;
        public string? SelectedExpLoopSegment
        {
            get => _selectedExpLoopSegment;
            set => SetProperty(ref _selectedExpLoopSegment, value);
        }

        private string? _selectedStartRouteSegment;
        public string? SelectedStartRouteSegment
        {
            get => _selectedStartRouteSegment;
            set => SetProperty(ref _selectedStartRouteSegment, value);
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
                    OnPropertyChanged(nameof(CityMapNumber));
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
                    RefreshHuntList();
                    RefreshStartRouteList();
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

        public int CityMapNumber
        {
            get => CurrentProfile?.CityMapNumber ?? 0;
            set { if (CurrentProfile != null) { CurrentProfile.CityMapNumber = value; OnPropertyChanged(); } }
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
            set { if (CurrentProfile != null) { CurrentProfile.DefaultHuntName = value; OnPropertyChanged(); } }
        }

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

        // ──────────────────── Start route list ────────────────────

        private ObservableCollection<StartRouteViewModel> _startRoutes = new();
        public ObservableCollection<StartRouteViewModel> StartRoutes
        {
            get => _startRoutes;
            set => SetProperty(ref _startRoutes, value);
        }

        private StartRouteViewModel? _selectedStartRoute;
        public StartRouteViewModel? SelectedStartRoute
        {
            get => _selectedStartRoute;
            set
            {
                if (SetProperty(ref _selectedStartRoute, value))
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
        public ICommand AddStartRouteCommand { get; }
        public ICommand RemoveStartRouteCommand { get; }

        // ──────────────────── Implementation ────────────────────

        private void NewProfile()
        {
            CurrentProfile = new BotProfile
            {
                Name = "NewProfile",
                CityMapNumber = 1,
                MinHpPotions = BotConstants.Repot.DefaultMinHpPotions,
                MinManaPotions = BotConstants.Repot.DefaultMinManaPotions,
                MaxWeightRatio = BotConstants.Repot.DefaultMaxWeightRatio,
                MinHp = BotConstants.Repot.DefaultMinHp,
                MinMana = BotConstants.Repot.DefaultMinMana,
                DryRunRepot = false,
                TeleportKey = BotConstants.Workflow.DefaultTeleportKey,
                TeleportScanCode = BotConstants.Workflow.DefaultTeleportScanCode,
                MaxTeleportRetries = BotConstants.Repot.MaxTeleportRetries,
                WindowOffsetX = 0,
                WindowOffsetY = 0,
                StartRoutes = new List<StartRoute>(),
                HuntDefinitions = new List<HuntDefinition>(),
                DefaultHuntName = ""
            };
            SelectedProfileName = null;
            StatusText = "Created new profile. Fill in the fields and click Save.";
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

            // Sync hunt definitions from ViewModel list back to profile
            CurrentProfile.HuntDefinitions = HuntDefinitions
                .Select(h => new HuntDefinition
                {
                    Name = h.Name,
                    RepotToExpPath = h.RepotToExpPath,
                    ExpLoopPath = h.ExpLoopPath
                })
                .ToList();

            // Sync start routes
            CurrentProfile.StartRoutes = StartRoutes
                .Select(sr => new StartRoute
                {
                    Name = sr.Name,
                    Area = new StartArea
                    {
                        MapNumber = sr.MapNumber,
                        MinX = sr.MinX,
                        MaxX = sr.MaxX,
                        MinY = sr.MinY,
                        MaxY = sr.MaxY
                    },
                    PathFile = sr.PathFile
                })
                .ToList();

            string fileName = CurrentProfile.Name.Trim();
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                fileName += ".json";

            string path = Path.Combine(PROFILE_DIR, fileName);

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(CurrentProfile, options);
                File.WriteAllText(path, json);
                StatusText = $"Saved profile '{CurrentProfile.Name}' to {path}";
                RefreshProfiles();
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving profile: {ex.Message}";
            }
        }

        private void LoadSelectedProfile()
        {
            if (string.IsNullOrEmpty(SelectedProfileName))
                return;

            string fileName = SelectedProfileName;
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                fileName += ".json";

            string path = Path.Combine(PROFILE_DIR, fileName);
            if (!File.Exists(path))
            {
                StatusText = $"Profile file not found: {path}";
                RefreshProfiles();
                return;
            }

            try
            {
                string json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<BotProfile>(json);
                if (profile == null)
                {
                    StatusText = $"Failed to deserialize profile '{SelectedProfileName}'.";
                    return;
                }

                // Backward compatibility: convert legacy fields to HuntDefinition
                if ((profile.HuntDefinitions == null || profile.HuntDefinitions.Count == 0)
                    && (!string.IsNullOrWhiteSpace(profile.RepotToExpPath) || !string.IsNullOrWhiteSpace(profile.ExpLoopPath)))
                {
                    profile.HuntDefinitions = new List<HuntDefinition>
                    {
                        new HuntDefinition
                        {
                            Name = "Default",
                            RepotToExpPath = profile.RepotToExpPath ?? "",
                            ExpLoopPath = profile.ExpLoopPath ?? ""
                        }
                    };
                    profile.DefaultHuntName = "Default";
                }

                CurrentProfile = profile;
                StatusText = $"Loaded profile '{profile.Name}' ({profile.HuntDefinitions?.Count ?? 0} hunt(s), {profile.StartRoutes?.Count ?? 0} start route(s)).";
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading profile: {ex.Message}";
            }
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
            if (!Directory.Exists(PROFILE_DIR))
            {
                Directory.CreateDirectory(PROFILE_DIR);
                return;
            }

            foreach (var f in Directory.GetFiles(PROFILE_DIR, "*.json"))
            {
                ProfileNames.Add(Path.GetFileNameWithoutExtension(f));
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

        // ── Hunt management ──

        private void AddHunt()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile loaded. Create or load a profile first.";
                return;
            }

            string? repotToExp = SelectedRepotToExpSegment;
            string? expLoop = SelectedExpLoopSegment;
            bool hasRepotSegment = !string.IsNullOrEmpty(repotToExp);
            bool hasExpSegment = !string.IsNullOrEmpty(expLoop);

            if (!hasRepotSegment && !hasExpSegment)
            {
                StatusText = "Select at least one segment from the Available Segments list.";
                return;
            }

            // Generate a name from the selected segments
            string huntName;
            if (hasRepotSegment && hasExpSegment)
            {
                string repotName = Path.GetFileNameWithoutExtension(repotToExp!);
                string expName = Path.GetFileNameWithoutExtension(expLoop!);
                huntName = $"{repotName} + {expName}";
            }
            else if (hasRepotSegment)
            {
                huntName = Path.GetFileNameWithoutExtension(repotToExp!);
            }
            else
            {
                huntName = Path.GetFileNameWithoutExtension(expLoop!);
            }

            // Avoid duplicate names
            var existingNames = new HashSet<string>(HuntDefinitions.Select(h => h.Name));
            if (existingNames.Contains(huntName))
            {
                int counter = 2;
                while (existingNames.Contains($"{huntName} ({counter})"))
                    counter++;
                huntName = $"{huntName} ({counter})";
            }

            HuntDefinitions.Add(new HuntDefinitionViewModel
            {
                Name = huntName,
                RepotToExpPath = repotToExp ?? "",
                ExpLoopPath = expLoop ?? ""
            });

            StatusText = $"Added hunt '{huntName}'.";
        }

        private void RemoveHunt()
        {
            if (SelectedHunt != null)
            {
                string name = SelectedHunt.Name;
                HuntDefinitions.Remove(SelectedHunt);
                StatusText = $"Removed hunt '{name}'.";
            }
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

        private void RefreshHuntList()
        {
            HuntDefinitions.Clear();
            if (CurrentProfile?.HuntDefinitions == null) return;

            foreach (var h in CurrentProfile.HuntDefinitions)
            {
                HuntDefinitions.Add(new HuntDefinitionViewModel
                {
                    Name = h.Name,
                    RepotToExpPath = h.RepotToExpPath,
                    ExpLoopPath = h.ExpLoopPath
                });
            }
        }

        // ── Start route management ──

        private void AddStartRoute()
        {
            if (CurrentProfile == null)
            {
                StatusText = "No profile loaded. Create or load a profile first.";
                return;
            }

            string? segment = SelectedStartRouteSegment;
            if (string.IsNullOrEmpty(segment))
            {
                StatusText = "Select a segment from Available Segments for the start route.";
                return;
            }

            string routeName = Path.GetFileNameWithoutExtension(segment);

            // Avoid duplicate names
            var existingNames = new HashSet<string>(StartRoutes.Select(sr => sr.Name));
            if (existingNames.Contains(routeName))
            {
                int counter = 2;
                while (existingNames.Contains($"{routeName} ({counter})"))
                    counter++;
                routeName = $"{routeName} ({counter})";
            }

            StartRoutes.Add(new StartRouteViewModel
            {
                Name = routeName,
                MapNumber = CurrentProfile.CityMapNumber,
                MinX = 0,
                MaxX = 0,
                MinY = 0,
                MaxY = 0,
                PathFile = segment
            });

            StatusText = $"Added start route '{routeName}' with segment '{segment}'.";
        }

        private void RemoveStartRoute()
        {
            if (SelectedStartRoute != null)
            {
                string name = SelectedStartRoute.Name;
                StartRoutes.Remove(SelectedStartRoute);
                StatusText = $"Removed start route '{name}'.";
            }
        }

        private void RefreshStartRouteList()
        {
            StartRoutes.Clear();
            if (CurrentProfile?.StartRoutes == null) return;

            foreach (var sr in CurrentProfile.StartRoutes)
            {
                StartRoutes.Add(new StartRouteViewModel
                {
                    Name = sr.Name,
                    MapNumber = sr.Area?.MapNumber ?? 0,
                    MinX = sr.Area?.MinX ?? 0,
                    MaxX = sr.Area?.MaxX ?? 0,
                    MinY = sr.Area?.MinY ?? 0,
                    MaxY = sr.Area?.MaxY ?? 0,
                    PathFile = sr.PathFile
                });
            }
        }
    }

    // ──────────────────── Helper ViewModels ────────────────────

    public class HuntDefinitionViewModel : BaseViewModel
    {
        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private string _repotToExpPath = "";
        public string RepotToExpPath
        {
            get => _repotToExpPath;
            set => SetProperty(ref _repotToExpPath, value);
        }

        private string _expLoopPath = "";
        public string ExpLoopPath
        {
            get => _expLoopPath;
            set => SetProperty(ref _expLoopPath, value);
        }
    }

    public class StartRouteViewModel : BaseViewModel
    {
        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private int _mapNumber;
        public int MapNumber
        {
            get => _mapNumber;
            set => SetProperty(ref _mapNumber, value);
        }

        private float _minX;
        public float MinX
        {
            get => _minX;
            set => SetProperty(ref _minX, value);
        }

        private float _maxX;
        public float MaxX
        {
            get => _maxX;
            set => SetProperty(ref _maxX, value);
        }

        private float _minY;
        public float MinY
        {
            get => _minY;
            set => SetProperty(ref _minY, value);
        }

        private float _maxY;
        public float MaxY
        {
            get => _maxY;
            set => SetProperty(ref _maxY, value);
        }

        private string _pathFile = "";
        public string PathFile
        {
            get => _pathFile;
            set => SetProperty(ref _pathFile, value);
        }
    }
}
