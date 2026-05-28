using DriverScanTester.Models;
using DriverScanTester.Utils;
using System;
using System.Windows.Input;

namespace DriverScanTester.ViewModels
{
    public sealed class BotViewModel : BaseViewModel
    {
        private readonly MainViewModel _main;
        private readonly Action<string> _appendLog;

        private string _hpThresholdText = "250";
        private string _manaThresholdText = "50";
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

        public BotViewModel(MainViewModel main, Action<string> appendLog)
        {
            _main = main;
            _appendLog = appendLog;

            RunBotCommand = new RelayCommand(_ => RunBot(), _ => _main.IsAttached);
            StopBotCommand = new RelayCommand(_ => StopAllBots(), _ => _main.IsAttached && (IsMovementBotRunning || IsHealManaBotRunning || IsLootBotRunning));
            ToggleHealManaBotCommand = new RelayCommand(_ => ToggleHealManaBot(), _ => _main.IsAttached);
            ToggleLootBotCommand = new RelayCommand(_ => ToggleLootBot(), _ => _main.IsAttached);
            OpenPathEditorCommand = new RelayCommand(_ => _main.OpenPathEditorInternal(), _ => _main.IsAttached);
            TestSellCommand = new RelayCommand(_ => {
                int ox = 0, oy = 0;
                if (!string.IsNullOrEmpty(SelectedProfileName))
                {
                    var p = _main.LoadProfile(SelectedProfileName);
                    if (p != null) { ox = p.WindowOffsetX; oy = p.WindowOffsetY; }
                }
                _main.TestSell(ox, oy);
            }, _ => _main.IsAttached);
            ClearBotLogCommand = new RelayCommand(_ => BotLogText = "");

            // 3-Phase Workflow commands
            StartWorkflowCommand = new RelayCommand(_ => StartWorkflowWithProfile(), _ => _main.IsAttached);
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
        public ICommand TestSellCommand { get; }
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
                    OnPropertyChanged(nameof(CanStartWorkflow));
            }
        }

        public string ValidationText
        {
            get => _validationText;
            set => SetProperty(ref _validationText, value);
        }

        public bool CanStartWorkflow => !string.IsNullOrEmpty(SelectedProfileName);

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

        private void StartWorkflowWithProfile()
        {
            BotProfile? profile = null;

            if (!string.IsNullOrEmpty(SelectedProfileName))
            {
                profile = _main.LoadProfile(SelectedProfileName);
                if (profile == null)
                {
                    _appendLog($"Failed to load profile '{SelectedProfileName}'. Starting without profile.");
                }
                else
                {
                    // Validate before starting
                    var errors = _main.ValidateProfile(profile);
                    if (errors.Count > 0)
                    {
                        _appendLog($"Profile validation FAILED for '{profile.Name}':");
                        foreach (var err in errors)
                            _appendLog($"  - {err}");
                        _appendLog("Workflow NOT started. Fix profile errors first.");
                        ValidationText = "Validation FAILED — check main log.";
                        return;
                    }
                }
            }

            _main.StartWorkflow(profile);

            if (profile != null)
                _appendLog($"Workflow started with profile '{profile.Name}'.");
            else
                _appendLog("Workflow started (no profile).");
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
