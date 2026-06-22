using DriverScanTester.Models;
using DriverScanTester.Utils;
using System;
using System.Windows.Input;
using static DriverScanTester.BotConstants;

namespace DriverScanTester.ViewModels
{
    public sealed class BotViewModel : BaseViewModel
    {
        private readonly MainViewModel _main;
        private readonly Action<string> _appendLog;

        private string _hpThresholdText = HealMana.HpThreshold.ToString();
        private string _manaThresholdText = HealMana.MpThreshold.ToString();
        private string _manualSellSlotText = "6";
        private string _currentHp = "--";
        private string _currentMana = "--";
        private string _hpPotCount = "--";
        private string _manaPotCount = "--";
        private string _botLogText = "";
        private bool _isMovementBotRunning;
        private bool _isHealManaBotRunning;
        private bool _isLootBotRunning;
        private bool _isWorkflowRunning;
        private string _workflowPhaseText = "Idle";
        private System.Threading.Timer _statsTimer;

        // Profile selection
        private System.Collections.ObjectModel.ObservableCollection<string> _profileNames = new();
        private string? _selectedProfileName;
        private string _validationText = "";

        // Hunt selection (within selected profile)
        private System.Collections.ObjectModel.ObservableCollection<string> _huntNames = new();
        private string? _selectedHuntName;

        public BotViewModel(MainViewModel main, Action<string> appendLog)
        {
            _main = main;
            _appendLog = appendLog;

            RunBotCommand = new RelayCommand(_ => RunBot(), _ => _main.IsAttached);
            StopBotCommand = new RelayCommand(_ => StopAllBots(), _ => _main.IsAttached && (IsMovementBotRunning || IsHealManaBotRunning || IsLootBotRunning));
            ToggleHealManaBotCommand = new RelayCommand(_ => ToggleHealManaBot(), _ => _main.IsAttached);
            ToggleLootBotCommand = new RelayCommand(_ => ToggleLootBot(), _ => _main.IsAttached);
            OpenPathEditorCommand = new RelayCommand(_ => _main.OpenPathEditorInternal(), _ => _main.IsAttached);
            TestLootCommand = new RelayCommand(_ => _main.TestLootScan(), _ => _main.IsAttached);
            TestScanAreaCommand = new RelayCommand(_ => _main.TestScanArea(), _ => _main.IsAttached);
            TestSellCommand = new RelayCommand(_ => {
                int ox = 0, oy = 0;
                if (!string.IsNullOrEmpty(SelectedProfileName))
                {
                    var p = _main.LoadProfile(SelectedProfileName);
                    if (p != null) { ox = p.WindowOffsetX; oy = p.WindowOffsetY; }
                }
                _main.TestSell(ox, oy);
            }, _ => _main.IsAttached);
            TestSellSpecificSlotCommand = new RelayCommand(_ => {
                if (int.TryParse(ManualSellSlotText, out int slot))
                {
                    int ox = 0, oy = 0;
                    if (!string.IsNullOrEmpty(SelectedProfileName))
                    {
                        var p = _main.LoadProfile(SelectedProfileName);
                        if (p != null) { ox = p.WindowOffsetX; oy = p.WindowOffsetY; }
                    }
                    _main.TestSellSpecificSlot(slot, ox, oy);
                }
                else
                {
                    _appendLog("Invalid slot number for Test Sell.");
                }
            }, _ => _main.IsAttached);
            ClearBotLogCommand = new RelayCommand(_ => BotLogText = "");

            // 3-Phase Workflow commands
            StartWorkflowCommand = new RelayCommand(_ => StartWorkflowWithProfile(), _ => _main.IsAttached && CanStartWorkflow);
            StopWorkflowCommand = new RelayCommand(_ => _main.StopWorkflow(), _ => _main.IsAttached);
            RefreshProfilesCommand = new RelayCommand(_ => RefreshProfiles(), _ => _main.IsAttached);
            ValidateProfileCommand = new RelayCommand(_ => ValidateSelectedProfile(), _ => _main.IsAttached);

            _statsTimer = new System.Threading.Timer(_ => RefreshStats(), null, 0, 1000);
        }

        public string HpThresholdText
        {
            get => _hpThresholdText;
            set
            {
                if (SetProperty(ref _hpThresholdText, value))
                {
                    if (short.TryParse(value, out short val))
                        _main.HealManaThreshold1 = val;
                }
            }
        }

        public string ManaThresholdText
        {
            get => _manaThresholdText;
            set
            {
                if (SetProperty(ref _manaThresholdText, value))
                {
                    if (short.TryParse(value, out short val))
                        _main.HealManaThreshold2 = val;
                }
            }
        }

        public string ManualSellSlotText
        {
            get => _manualSellSlotText;
            set => SetProperty(ref _manualSellSlotText, value);
        }

        public string CurrentHp
        {
            get => _currentHp;
            set => SetProperty(ref _currentHp, value);
        }

        public string CurrentMana
        {
            get => _currentMana;
            set => SetProperty(ref _currentMana, value);
        }

        public string HpPotCount
        {
            get => _hpPotCount;
            set => SetProperty(ref _hpPotCount, value);
        }

        public string ManaPotCount
        {
            get => _manaPotCount;
            set => SetProperty(ref _manaPotCount, value);
        }

        public string BotLogText
        {
            get => _botLogText;
            set => SetProperty(ref _botLogText, value);
        }

        public bool IsMovementBotRunning
        {
            get => _isMovementBotRunning;
            set
            {
                if (SetProperty(ref _isMovementBotRunning, value))
                    OnPropertyChanged(nameof(IsAnyBotRunning));
            }
        }

        public bool IsHealManaBotRunning
        {
            get => _isHealManaBotRunning;
            set
            {
                if (SetProperty(ref _isHealManaBotRunning, value))
                    OnPropertyChanged(nameof(IsAnyBotRunning));
            }
        }

        public bool IsLootBotRunning
        {
            get => _isLootBotRunning;
            set
            {
                if (SetProperty(ref _isLootBotRunning, value))
                    OnPropertyChanged(nameof(IsAnyBotRunning));
            }
        }

        public bool IsAnyBotRunning => IsMovementBotRunning || IsHealManaBotRunning || IsLootBotRunning;

        // --- 3-Phase Workflow properties ---
        public bool IsWorkflowRunning
        {
            get => _isWorkflowRunning;
            set => SetProperty(ref _isWorkflowRunning, value);
        }

        public string WorkflowPhaseText
        {
            get => _workflowPhaseText;
            set => SetProperty(ref _workflowPhaseText, value);
        }

        public ICommand RunBotCommand { get; }
        public ICommand StopBotCommand { get; }
        public ICommand ToggleHealManaBotCommand { get; }
        public ICommand ToggleLootBotCommand { get; }
        public ICommand OpenPathEditorCommand { get; }
        public ICommand TestLootCommand { get; }
        public ICommand TestScanAreaCommand { get; }
        public ICommand TestSellCommand { get; }
        public ICommand TestSellSpecificSlotCommand { get; }
        public ICommand ClearBotLogCommand { get; }

        // 3-Phase Workflow commands
        public ICommand StartWorkflowCommand { get; }
        public ICommand StopWorkflowCommand { get; }
        public ICommand RefreshProfilesCommand { get; }
        public ICommand ValidateProfileCommand { get; }

        // Profile selection
        public System.Collections.ObjectModel.ObservableCollection<string> ProfileNames
        {
            get => _profileNames;
            set => SetProperty(ref _profileNames, value);
        }

        public string? SelectedProfileName
        {
            get => _selectedProfileName;
            set
            {
                if (SetProperty(ref _selectedProfileName, value))
                {
                    OnProfileChanged();
                    OnPropertyChanged(nameof(CanStartWorkflow));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string ValidationText
        {
            get => _validationText;
            set => SetProperty(ref _validationText, value);
        }

        // Hunt selection (within selected profile)
        public System.Collections.ObjectModel.ObservableCollection<string> HuntNames
        {
            get => _huntNames;
            set => SetProperty(ref _huntNames, value);
        }

        public string? SelectedHuntName
        {
            get => _selectedHuntName;
            set
            {
                if (SetProperty(ref _selectedHuntName, value))
                {
                    OnPropertyChanged(nameof(CanStartWorkflow));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool CanStartWorkflow => !string.IsNullOrEmpty(SelectedProfileName) && !string.IsNullOrEmpty(SelectedHuntName);

        private void RunBot()
        {
            _main.RunBot();
        }

        private void StopAllBots()
        {
            _main.StopAllBotsInternal();
        }

        private void ToggleHealManaBot()
        {
            _main.ToggleHealManaBotInternal();
        }

        private void ToggleLootBot()
        {
            _main.ToggleLootBotInternal();
        }

        private void RefreshProfiles()
        {
            var names = _main.ListProfiles();
            ProfileNames.Clear();
            foreach (var n in names)
                ProfileNames.Add(n);

            // Refresh hunt names for the currently selected profile
            if (!string.IsNullOrEmpty(SelectedProfileName))
                OnProfileChanged();

            ValidationText = $"Found {names.Count} profile(s).";
        }

        private void ValidateSelectedProfile()
        {
            if (string.IsNullOrEmpty(SelectedProfileName))
            {
                ValidationText = "No profile selected.";
                return;
            }

            var profile = _main.LoadProfile(SelectedProfileName);
            if (profile == null)
            {
                ValidationText = $"Failed to load profile '{SelectedProfileName}'.";
                return;
            }

            var errors = _main.ValidateProfile(profile);
            if (errors.Count == 0)
            {
                ValidationText = $"Profile '{profile.Name}' is valid.";
                _appendLog($"Profile '{profile.Name}' is valid.");
            }
            else
            {
                ValidationText = $"Profile '{profile.Name}' validation ERRORS:";
                _appendLog($"Profile '{profile.Name}' validation ERRORS:");
                foreach (var err in errors)
                {
                    ValidationText += "\n" + err;
                    _appendLog($"  - {err}");
                }
            }
        }

        /// <summary>
        /// Called when the user selects a different profile. Loads the profile
        /// and populates the HuntNames list from its HuntDefinitions.
        /// </summary>
        private void OnProfileChanged()
        {
            HuntNames.Clear();
            SelectedHuntName = null;

            if (string.IsNullOrEmpty(SelectedProfileName))
                return;

            var profile = _main.LoadProfile(SelectedProfileName);
            if (profile == null || profile.HuntDefinitions == null || profile.HuntDefinitions.Count == 0)
            {
                _appendLog($"Profile '{SelectedProfileName}' has no HuntDefinitions.");
                return;
            }

            foreach (var hunt in profile.HuntDefinitions)
            {
                if (!string.IsNullOrWhiteSpace(hunt.Name))
                    HuntNames.Add(hunt.Name);
            }

            // If DefaultHuntName matches an existing hunt, select it; otherwise select the first one
            if (!string.IsNullOrEmpty(profile.DefaultHuntName) && HuntNames.Contains(profile.DefaultHuntName))
                SelectedHuntName = profile.DefaultHuntName;
            else if (HuntNames.Count > 0)
                SelectedHuntName = HuntNames[0];
        }

        private void StartWorkflowWithProfile()
        {
            BotProfile? profile = null;
            HuntDefinition? activeHunt = null;

            if (!string.IsNullOrEmpty(SelectedProfileName))
            {
                profile = _main.LoadProfile(SelectedProfileName);
                if (profile == null)
                {
                    _appendLog($"Failed to load profile '{SelectedProfileName}'. Starting without profile.");
                }
                else
                {
                    // Resolve the selected hunt
                    if (!string.IsNullOrEmpty(SelectedHuntName) && profile.HuntDefinitions != null)
                        activeHunt = profile.HuntDefinitions.Find(h => h.Name == SelectedHuntName);

                    // If the selected hunt name doesn't exist, try DefaultHuntName or first hunt
                    if (activeHunt == null && profile.HuntDefinitions != null && profile.HuntDefinitions.Count > 0)
                    {
                        if (!string.IsNullOrEmpty(profile.DefaultHuntName))
                        {
                            activeHunt = profile.HuntDefinitions.Find(h => h.Name == profile.DefaultHuntName);
                            if (activeHunt != null)
                                _appendLog($"Selected hunt '{SelectedHuntName}' not found; using DefaultHuntName '{activeHunt.Name}'.");
                        }

                        if (activeHunt == null)
                        {
                            activeHunt = profile.HuntDefinitions[0];
                            _appendLog($"Selected hunt '{SelectedHuntName}' not found; using first hunt '{activeHunt.Name}'.");
                        }
                    }

                    // Require active hunt when profile is loaded
                    if (activeHunt == null)
                    {
                        _appendLog("No active hunt selected. Workflow cannot start.");
                        ValidationText = "No active hunt selected.";
                        return;
                    }

                    // Validate before starting
                    var errors = _main.ValidateProfile(profile);
                    if (errors.Count > 0)
                    {
                        _appendLog("Profile validation failed:");
                        foreach (var error in errors)
                            _appendLog(" - " + error);
                        _appendLog("Workflow NOT started. Fix profile errors first.");
                        ValidationText = "Validation FAILED — check main log.";
                        return;
                    }
                }
            }

            // Single call — profile+activeHunt are paired consistently
            _main.StartWorkflow(profile, activeHunt);

            if (profile != null)
                _appendLog($"Workflow started with profile '{profile.Name}'.");
            else
                _appendLog("Workflow started (no profile).");
            if (activeHunt != null)
                _appendLog($"Active hunt: '{activeHunt.Name}' (RepotToExpPath: '{activeHunt.RepotToExpPath}', ExpLoopPath: '{activeHunt.ExpLoopPath}')");
        }

        public void SyncBotStates()
        {
            IsMovementBotRunning = _main.IsMovementBotRunningInternal;
            IsHealManaBotRunning = _main.IsHealManaBotRunningInternal;
            IsLootBotRunning = _main.IsLootBotRunningInternal;

            HpThresholdText = _main.HealManaThreshold1.ToString();
            ManaThresholdText = _main.HealManaThreshold2.ToString();
        }

        private void RefreshStats()
        {
            if (!_main.IsAttached)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    CurrentHp = "--";
                    CurrentMana = "--";
                    HpPotCount = "--";
                    ManaPotCount = "--";
                });
                return;
            }

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                var (hp, mana, success) = _main.GetHpMana();
                if (success)
                {
                    CurrentHp = hp.ToString();
                    CurrentMana = mana.ToString();
                }
                HpPotCount = _main.GetHpPotionCount().ToString();
                ManaPotCount = _main.GetManaPotionCount().ToString();

                IsMovementBotRunning = _main.IsMovementBotRunningInternal;
                IsHealManaBotRunning = _main.IsHealManaBotRunningInternal;
                IsLootBotRunning = _main.IsLootBotRunningInternal;

                // Sync workflow state from MainViewModel
                IsWorkflowRunning = _main.IsWorkflowRunning;
                WorkflowPhaseText = _main.WorkflowPhaseText;
            });
        }

        public void AppendBotLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            BotLogText += $"[{stamp}] {line}\r\n";
        }
    }
}
