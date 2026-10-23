using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.Utils;
using System;
using System.Linq;
using System.Windows.Input;
using static DriverScanTester.BotConstants;

namespace DriverScanTester.ViewModels
{
    public sealed class BotViewModel : BaseViewModel, IDisposable
    {
        private readonly MainViewModel _main;

        private string _hpThresholdText = HealMana.HpThreshold.ToString();
        private string _manaThresholdText = HealMana.MpThreshold.ToString();
        private string _currentHp = "--";
        private string _currentMana = "--";
        private string _hpPotCount = "--";
        private string _manaPotCount = "--";
        private string _botLogText = "";
        private readonly Queue<string> _botLogLines = new();
        private readonly object _botLogLock = new();
        private bool _isMovementBotRunning;
        private bool _isHealManaBotRunning;
        private bool _isLootBotRunning;
        private bool _isWorkflowRunning;
        private bool _isBotPaused;
        private bool _isGameAttached;
        private string _workflowPhaseText = "Idle";
        private System.Threading.Timer? _statsTimer;
        private int _isDisposed;

        // Test Method control
        private System.Collections.ObjectModel.ObservableCollection<string> _availableOperationNames = new();
        private string? _selectedOperationName;
        private string _operationTestStatus = "Idle";
        private bool _isOperationTestRunning;

        // Loot diagnostic test (Tools tab)
        private string _lootScanTestStatus = "Idle";
        private bool _isLootScanTestRunning;

        // Movement Synchronization Diagnostic (Tools tab). One experiment at a time,
        // explicit user start/stop, and a passive post-test observation phase.
        private readonly System.Collections.ObjectModel.ObservableCollection<MovementDiagnosticScenarioInfo> _movementDiagnosticScenarios = new();
        private readonly System.Collections.ObjectModel.ObservableCollection<MovementDiagnosticObservationChoice> _movementDiagnosticObservationChoices = new();
        private MovementDiagnosticScenarioInfo? _selectedMovementDiagnosticScenario;
        private MovementDiagnosticObservationChoice? _selectedMovementDiagnosticObservation;
        private bool _movementDiagnosticAdvancedMode;
        private string _movementDiagnosticDurationText = "6";
        private string _movementDiagnosticRepeatIntervalText = "150";
        private string _movementDiagnosticWaypointXText = "";
        private string _movementDiagnosticWaypointYText = "";
        private string _movementDiagnosticNotesText = "";
        private bool _movementDiagnosticPersistedAfterStop;
        private bool _movementDiagnosticObservedDuringManualMovement;
        private string _movementDiagnosticStateText = "Idle";
        private string _movementDiagnosticMessageText = "Select a scenario and press Start Test.";
        private string _movementDiagnosticElapsedText = "00:00.0";
        private string _movementDiagnosticCountdownText = "";
        private string _movementDiagnosticProgressText = "";
        private string _movementDiagnosticInputMethodText = "—";
        private string _movementDiagnosticLastResultText = "No test run yet.";
        private string _movementDiagnosticCheckpointText = "";
        private string _movementDiagnosticRecoveryText = "Unknown (not marked).";
        private string _movementDiagnosticReportDirectoryText = "—";
        private bool _isMovementDiagnosticBusy;
        private bool _isMovementDiagnosticObservationActive;

        // Profile selection
        private System.Collections.ObjectModel.ObservableCollection<string> _profileNames = new();
        private string? _selectedProfileName;
        private string _validationText = "";

        // Run-duration timer (minutes). Empty / 0 / invalid = run indefinitely.
        private string _runDurationMinutesText = "";
        private string _runTimeRemainingText = "unlimited";

        public BotViewModel(MainViewModel main)
        {
            _main = main;
            _hpThresholdText = _main.HealManaThreshold1.ToString();
            _manaThresholdText = _main.HealManaThreshold2.ToString();

            // Game-dependent commands stay gated by attachment; everything that is just
            // configuration/logging keeps working while detached (the whole point of the
            // offline mode). Stop/Pause/Resume deliberately never require attachment so a
            // running bot can always be stopped.
            RunBotCommand = new RelayCommand(_ => RunBot(), _ => _main.IsAttached && !_main.MovementDiagnostic.BlocksOtherRuntimes);
            StopBotCommand = new RelayCommand(_ => StopAllBots(), _ => IsAnyRuntimeActive);
            PauseBotCommand = new RelayCommand(_ => PauseBots(), _ => !IsBotPaused && IsAnyRuntimeActive);
            ResumeBotCommand = new RelayCommand(_ => ResumeBots(), _ => IsBotPaused);
            ToggleHealManaBotCommand = new RelayCommand(_ => ToggleHealManaBot(), _ => (_main.IsAttached && !_main.MovementDiagnostic.BlocksOtherRuntimes) || IsHealManaBotRunning);
            OpenPathEditorCommand = new RelayCommand(_ => _main.OpenPathEditorInternal());
            CaptureNpcMouseOverCommand = new RelayCommand(_ => _main.CaptureNpcMouseOver(), _ => _main.IsAttached);
            CaptureItemMouseOverCommand = new RelayCommand(_ => _main.CaptureItemMouseOver(), _ => _main.IsAttached);
            TestOperationCommand = new RelayCommand(_ => RunOperationTest(), _ => _main.IsAttached && !IsOperationTestRunning && !_main.MovementDiagnostic.BlocksOtherRuntimes);
            // Movement/loot/workflow own the camera and cursor, so they must be stopped.
            // The heal bot is independent (potions/HP only) and may keep running.
            TestLootSodSopCommand = new RelayCommand(
                _ => RunLootScanTest(LootSystem.LootDiagnosticMode.SodSopPink),
                _ => _main.IsAttached && !IsLootScanTestRunning &&
                     !_main.MovementDiagnostic.BlocksOtherRuntimes &&
                     !IsMovementBotRunning && !IsLootBotRunning && !IsWorkflowRunning);
            TestLootNormalCommand = new RelayCommand(
                _ => RunLootScanTest(LootSystem.LootDiagnosticMode.NormalLoot),
                _ => _main.IsAttached && !IsLootScanTestRunning &&
                     !_main.MovementDiagnostic.BlocksOtherRuntimes &&
                     !IsMovementBotRunning && !IsLootBotRunning && !IsWorkflowRunning);
            ClearBotLogCommand = new RelayCommand(_ => ClearBotLog());

            // Movement Synchronization Diagnostic (Tools tab)
            BuildMovementDiagnosticObservationChoices();
            RebuildMovementDiagnosticScenarios();
            if (_selectedMovementDiagnosticScenario?.Scenario == MovementDiagnosticScenario.ManualKeyboardBaseline)
                _movementDiagnosticDurationText = "15";
            StartMovementDiagnosticCommand = new RelayCommand(
                _ => _ = StartMovementDiagnosticAsync(),
                _ => _main.IsAttached &&
                     !_main.MovementDiagnostic.BlocksOtherRuntimes &&
                     !_isOperationTestRunning && !_isLootScanTestRunning);
            StopMovementDiagnosticCommand = new RelayCommand(
                _ => _ = StopMovementDiagnosticAsync(),
                _ => _main.MovementDiagnostic.IsTestActive);
            StartMovementDiagnosticObservationCommand = new RelayCommand(
                _ => _ = StartMovementDiagnosticObservationAsync(),
                _ => _main.MovementDiagnostic.CanStartObservation);
            StopMovementDiagnosticObservationCommand = new RelayCommand(
                _ => _ = StopMovementDiagnosticObservationAsync(),
                _ => _main.MovementDiagnostic.IsObservationActive);
            MarkMovementRecoveryCommand = new RelayCommand(
                _ => MarkMovementRecovery(),
                _ => _main.MovementDiagnostic.CanMarkRecovery);
            RecordMovementDiagnosticObservationCommand = new RelayCommand(
                _ => RecordMovementDiagnosticObservation(),
                _ => _main.MovementDiagnostic.HasCurrentSession);
            ExportMovementDiagnosticReportCommand = new RelayCommand(
                _ => _ = ExportMovementDiagnosticReportAsync(),
                _ => _main.MovementDiagnostic.HasCurrentSession);

            // Route Workflow commands
            StartWorkflowCommand = new RelayCommand(_ => StartWorkflowWithProfile(), _ => _main.IsAttached && CanStartWorkflow && !IsBotPaused && !_main.MovementDiagnostic.BlocksOtherRuntimes);
            StopWorkflowCommand = new RelayCommand(_ => _main.StopWorkflow(), _ => IsWorkflowRunning);
            PauseWorkflowCommand = new RelayCommand(_ => PauseBots(), _ => !IsBotPaused && IsWorkflowRunning);
            ResumeWorkflowCommand = new RelayCommand(_ => ResumeBots(), _ => IsBotPaused && IsWorkflowRunning);
            RefreshProfilesCommand = new RelayCommand(_ => RefreshProfiles());
            ValidateProfileCommand = new RelayCommand(_ => ValidateSelectedProfile(), _ => !string.IsNullOrWhiteSpace(SelectedProfileName));

            BuildAvailableOperationNames();

            // Load the saved profiles immediately so the list is populated when the
            // window opens — no manual Refresh click needed (Refresh still exists for
            // picking up profiles added while the window is open).
            RefreshProfiles();

            // The bot log lives in the shared service so it survives closing/reopening
            // the panel and is recorded even while detached.
            SeedBotLogFromService();
            BotLogService.Instance.LineAppended += OnBotLogLine;

            IsGameAttached = _main.IsAttached;
            _main.AttachmentChanged += OnMainAttachmentChanged;
            _main.PropertyChanged += OnMainPropertyChanged;

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

        /// <summary>Global runtime-only option; profiles and paths never own this value.</summary>
        public bool MobGroupingEnabled
        {
            get => MobGroupingRuntimeSettings.Enabled;
            set
            {
                if (MobGroupingRuntimeSettings.Enabled == value)
                    return;
                MobGroupingRuntimeSettings.Enabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MobGroupingStatus));
                AppendBotLog($"[MobGrouping] global runtime setting {(value ? "enabled" : "disabled")}.");
            }
        }

        public string MobGroupingStatus => MobGroupingRuntimeSettings.Enabled
            ? $"Mob Grouping: {_main.MobGroupingStatus}"
            : "Mob Grouping: Off";

        public bool IsMovementBotRunning
        {
            get => _isMovementBotRunning;
            set
            {
                if (SetProperty(ref _isMovementBotRunning, value))
                {
                    OnPropertyChanged(nameof(IsAnyBotRunning));
                    NotifyRuntimeActivityChanged();
                }
            }
        }

        public bool IsHealManaBotRunning
        {
            get => _isHealManaBotRunning;
            set
            {
                if (SetProperty(ref _isHealManaBotRunning, value))
                {
                    OnPropertyChanged(nameof(IsAnyBotRunning));
                    NotifyRuntimeActivityChanged();
                }
            }
        }

        public bool IsLootBotRunning
        {
            get => _isLootBotRunning;
            set
            {
                if (SetProperty(ref _isLootBotRunning, value))
                {
                    OnPropertyChanged(nameof(IsAnyBotRunning));
                    NotifyRuntimeActivityChanged();
                }
            }
        }

        public bool IsAnyBotRunning => IsMovementBotRunning || IsHealManaBotRunning || IsLootBotRunning;

        public bool IsAnyRuntimeActive => IsAnyBotRunning || IsWorkflowRunning;

        private void NotifyRuntimeActivityChanged()
        {
            OnPropertyChanged(nameof(IsAnyRuntimeActive));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>True while every bot loop is suspended in place (Pause pressed).</summary>
        public bool IsBotPaused
        {
            get => _isBotPaused;
            set
            {
                if (SetProperty(ref _isBotPaused, value))
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        // Route Workflow properties
        public bool IsWorkflowRunning
        {
            get => _isWorkflowRunning;
            set
            {
                if (SetProperty(ref _isWorkflowRunning, value))
                    NotifyRuntimeActivityChanged();
            }
        }

        public string WorkflowPhaseText
        {
            get => _workflowPhaseText;
            set => SetProperty(ref _workflowPhaseText, value);
        }

        /// <summary>Completed repot-&gt;exp cycles of the running workflow (0 while idle).</summary>
        public int WorkflowCycleCount => _main.WorkflowCycleCount;

        /// <summary>
        /// Pass-through for the "stop and teleport to city when another player is detected"
        /// option. Kept editable while detached so profiles can be prepared offline.
        /// </summary>
        public bool StopOnOtherPlayerDetected
        {
            get => _main.StopOnOtherPlayerDetected;
            set
            {
                if (_main.StopOnOtherPlayerDetected == value) return;
                _main.StopOnOtherPlayerDetected = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// True while a game process is attached. False means "configuration mode":
        /// profile/path editing, validation and the bot log work; start/capture actions
        /// unlock after Attach.
        /// </summary>
        public bool IsGameAttached
        {
            get => _isGameAttached;
            private set
            {
                if (SetProperty(ref _isGameAttached, value))
                {
                    OnPropertyChanged(nameof(AttachmentStatusText));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>Human-readable attachment state shown in the Bot panel banner.</summary>
        public string AttachmentStatusText => IsGameAttached
            ? "Game attached — all bot actions are available."
            : "Game not attached — you can still edit paths/profiles, validate them and read the bot log. Start/capture actions unlock after Attach.";

        /// <summary>Attach button of the main window, exposed so the Bot panel can attach the game itself.</summary>
        public ICommand AttachCommand => _main.AttachCommand;

        /// <summary>
        /// PID shared with the main window. The Bot panel can be used standalone: type the
        /// game PID and press Attach without touching the main window.
        /// </summary>
        public string PidText
        {
            get => _main.PidText;
            set
            {
                if (_main.PidText == value) return;
                _main.PidText = value;
                OnPropertyChanged();
            }
        }

        private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.PidText))
                OnPropertyChanged(nameof(PidText));
        }

        public ICommand RunBotCommand { get; }
        public ICommand StopBotCommand { get; }
        public ICommand PauseBotCommand { get; }
        public ICommand ResumeBotCommand { get; }
        public ICommand ToggleHealManaBotCommand { get; }
        public ICommand OpenPathEditorCommand { get; }
        public ICommand CaptureNpcMouseOverCommand { get; }
        public ICommand CaptureItemMouseOverCommand { get; }
        public ICommand TestOperationCommand { get; }
        public ICommand TestLootSodSopCommand { get; }
        public ICommand TestLootNormalCommand { get; }
        public ICommand ClearBotLogCommand { get; }

        // Movement Synchronization Diagnostic (Tools tab)
        public ICommand StartMovementDiagnosticCommand { get; }
        public ICommand StopMovementDiagnosticCommand { get; }
        public ICommand StartMovementDiagnosticObservationCommand { get; }
        public ICommand StopMovementDiagnosticObservationCommand { get; }
        public ICommand MarkMovementRecoveryCommand { get; }
        public ICommand RecordMovementDiagnosticObservationCommand { get; }
        public ICommand ExportMovementDiagnosticReportCommand { get; }

        // Route Workflow commands
        public ICommand StartWorkflowCommand { get; }
        public ICommand StopWorkflowCommand { get; }
        public ICommand PauseWorkflowCommand { get; }
        public ICommand ResumeWorkflowCommand { get; }
        public ICommand RefreshProfilesCommand { get; }
        public ICommand ValidateProfileCommand { get; }

        // -----------------------------------------------------------------
        //  Movement Synchronization Diagnostic (Tools tab)
        // -----------------------------------------------------------------

        public System.Collections.ObjectModel.ObservableCollection<MovementDiagnosticScenarioInfo> MovementDiagnosticScenarios
            => _movementDiagnosticScenarios;

        public MovementDiagnosticScenarioInfo? SelectedMovementDiagnosticScenario
        {
            get => _selectedMovementDiagnosticScenario;
            set
            {
                if (!SetProperty(ref _selectedMovementDiagnosticScenario, value))
                    return;

                if (value != null)
                    MovementDiagnosticDurationText = value.Scenario == MovementDiagnosticScenario.ManualKeyboardBaseline
                        ? "15"
                        : "6";

                OnPropertyChanged(nameof(MovementDiagnosticSelectedInputMethodText));
                OnPropertyChanged(nameof(MovementDiagnosticScenarioHintText));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public string MovementDiagnosticSelectedInputMethodText =>
            _selectedMovementDiagnosticScenario?.InputMethodDescription ?? "—";

        public string MovementDiagnosticScenarioHintText => _selectedMovementDiagnosticScenario?.Scenario switch
        {
            MovementDiagnosticScenario.ManualKeyboardBaseline =>
                "No synthetic input. Move manually with W/A/D while this session records local state for comparison.",
            MovementDiagnosticScenario.KeybdEventRepeatedW =>
                "Repeated W-down events are not assumed to be equivalent to real hardware key repeat.",
            MovementDiagnosticScenario.KeybdEventPulsedW =>
                "Expected intermittent movement. This is a known-jerky reference, not proof of the original defect.",
            MovementDiagnosticScenario.DirectCameraMemorySteering =>
                "ADVANCED: performs one bounded camera-angle memory write plus readback, then a short W hold.",
            MovementDiagnosticScenario.ExistingMovementSystemOnlyMove =>
                "Requires a safe waypoint X/Y below, 0.5–30 units ahead. The unmodified MovementSystem is used without combat/loot/repot.",
            _ => "Independent experiment. The production movement system is not modified."
        };

        public System.Collections.ObjectModel.ObservableCollection<MovementDiagnosticObservationChoice> MovementDiagnosticObservationChoices
            => _movementDiagnosticObservationChoices;

        public MovementDiagnosticObservationChoice? SelectedMovementDiagnosticObservation
        {
            get => _selectedMovementDiagnosticObservation;
            set => SetProperty(ref _selectedMovementDiagnosticObservation, value);
        }

        public bool MovementDiagnosticAdvancedMode
        {
            get => _movementDiagnosticAdvancedMode;
            set
            {
                if (!SetProperty(ref _movementDiagnosticAdvancedMode, value))
                    return;

                RebuildMovementDiagnosticScenarios();
                OnPropertyChanged(nameof(MovementDiagnosticAdvancedHintText));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public string MovementDiagnosticAdvancedHintText => MovementDiagnosticAdvancedMode
            ? "Advanced mode enabled. DirectCamera memory steering is selectable and still requires an explicit Start Test."
            : "Advanced mode disabled. DirectCamera memory steering (TEST 8) is not available.";

        public string MovementDiagnosticDurationText
        {
            get => _movementDiagnosticDurationText;
            set => SetProperty(ref _movementDiagnosticDurationText, value);
        }

        public string MovementDiagnosticRepeatIntervalText
        {
            get => _movementDiagnosticRepeatIntervalText;
            set => SetProperty(ref _movementDiagnosticRepeatIntervalText, value);
        }

        public string MovementDiagnosticWaypointXText
        {
            get => _movementDiagnosticWaypointXText;
            set => SetProperty(ref _movementDiagnosticWaypointXText, value);
        }

        public string MovementDiagnosticWaypointYText
        {
            get => _movementDiagnosticWaypointYText;
            set => SetProperty(ref _movementDiagnosticWaypointYText, value);
        }

        public string MovementDiagnosticNotesText
        {
            get => _movementDiagnosticNotesText;
            set => SetProperty(ref _movementDiagnosticNotesText, value);
        }

        public bool MovementDiagnosticPersistedAfterStop
        {
            get => _movementDiagnosticPersistedAfterStop;
            set => SetProperty(ref _movementDiagnosticPersistedAfterStop, value);
        }

        public bool MovementDiagnosticObservedDuringManualMovement
        {
            get => _movementDiagnosticObservedDuringManualMovement;
            set => SetProperty(ref _movementDiagnosticObservedDuringManualMovement, value);
        }

        public string MovementDiagnosticStateText
        {
            get => _movementDiagnosticStateText;
            private set => SetProperty(ref _movementDiagnosticStateText, value);
        }

        public string MovementDiagnosticMessageText
        {
            get => _movementDiagnosticMessageText;
            private set => SetProperty(ref _movementDiagnosticMessageText, value);
        }

        public string MovementDiagnosticElapsedText
        {
            get => _movementDiagnosticElapsedText;
            private set => SetProperty(ref _movementDiagnosticElapsedText, value);
        }

        public string MovementDiagnosticCountdownText
        {
            get => _movementDiagnosticCountdownText;
            private set => SetProperty(ref _movementDiagnosticCountdownText, value);
        }

        public string MovementDiagnosticProgressText
        {
            get => _movementDiagnosticProgressText;
            private set => SetProperty(ref _movementDiagnosticProgressText, value);
        }

        public string MovementDiagnosticInputMethodText
        {
            get => _movementDiagnosticInputMethodText;
            private set => SetProperty(ref _movementDiagnosticInputMethodText, value);
        }

        public string MovementDiagnosticLastResultText
        {
            get => _movementDiagnosticLastResultText;
            private set => SetProperty(ref _movementDiagnosticLastResultText, value);
        }

        public string MovementDiagnosticCheckpointText
        {
            get => _movementDiagnosticCheckpointText;
            private set => SetProperty(ref _movementDiagnosticCheckpointText, value);
        }

        public string MovementDiagnosticRecoveryText
        {
            get => _movementDiagnosticRecoveryText;
            private set => SetProperty(ref _movementDiagnosticRecoveryText, value);
        }

        public string MovementDiagnosticReportDirectoryText
        {
            get => _movementDiagnosticReportDirectoryText;
            private set => SetProperty(ref _movementDiagnosticReportDirectoryText, value);
        }

        public bool IsMovementDiagnosticBusy
        {
            get => _isMovementDiagnosticBusy;
            private set => SetProperty(ref _isMovementDiagnosticBusy, value);
        }

        public bool IsMovementDiagnosticObservationActive
        {
            get => _isMovementDiagnosticObservationActive;
            private set => SetProperty(ref _isMovementDiagnosticObservationActive, value);
        }

        private void BuildMovementDiagnosticObservationChoices()
        {
            _movementDiagnosticObservationChoices.Clear();
            _movementDiagnosticObservationChoices.Add(new(MovementRemoteObservationRating.NotRecorded, "— not recorded —"));
            _movementDiagnosticObservationChoices.Add(new(MovementRemoteObservationRating.SmoothMovement, "Smooth movement"));
            _movementDiagnosticObservationChoices.Add(new(MovementRemoteObservationRating.AnimationStutteringOnly, "Stuttering animation only"));
            _movementDiagnosticObservationChoices.Add(new(MovementRemoteObservationRating.PositionJumping, "Position jumping / rubber-banding"));
            _movementDiagnosticObservationChoices.Add(new(MovementRemoteObservationRating.BothAnimationAndPosition, "Both animation and position affected"));
            _movementDiagnosticObservationChoices.Add(new(MovementRemoteObservationRating.CouldNotDetermine, "Could not determine"));
            _selectedMovementDiagnosticObservation = _movementDiagnosticObservationChoices[0];
        }

        private void RebuildMovementDiagnosticScenarios()
        {
            MovementDiagnosticScenario previous = _selectedMovementDiagnosticScenario?.Scenario
                ?? MovementDiagnosticScenario.ManualKeyboardBaseline;

            _movementDiagnosticScenarios.Clear();
            foreach (MovementDiagnosticScenarioInfo info in MovementDiagnosticScenarioCatalog.All)
            {
                if (info.RequiresAdvancedMode && !_movementDiagnosticAdvancedMode)
                    continue;
                _movementDiagnosticScenarios.Add(info);
            }

            MovementDiagnosticScenarioInfo? selected = _movementDiagnosticScenarios
                .FirstOrDefault(s => s.Scenario == previous);
            _selectedMovementDiagnosticScenario = selected ?? _movementDiagnosticScenarios[0];
            OnPropertyChanged(nameof(SelectedMovementDiagnosticScenario));
            OnPropertyChanged(nameof(MovementDiagnosticSelectedInputMethodText));
            OnPropertyChanged(nameof(MovementDiagnosticScenarioHintText));
        }

        private async Task StartMovementDiagnosticAsync()
        {
            if (_isOperationTestRunning || _isLootScanTestRunning)
            {
                MovementDiagnosticMessageText = "Stop the operation/loot diagnostic before starting the movement diagnostic.";
                AppendBotLog("[MovementDiag] Start refused: another Tools diagnostic is running.");
                return;
            }

            MovementDiagnosticScenarioInfo? info = SelectedMovementDiagnosticScenario;
            if (info == null)
            {
                MovementDiagnosticMessageText = "Select a diagnostic scenario first.";
                return;
            }

            if (!TryBuildMovementDiagnosticSettings(out MovementDiagnosticSettings? settings, out string error))
            {
                MovementDiagnosticMessageText = error;
                AppendBotLog($"[MovementDiag] Start refused: {error}");
                return;
            }

            MovementDiagnosticStartResult result =
                await _main.MovementDiagnostic.StartTestAsync(info.Scenario, settings);
            AppendBotLog($"[MovementDiag] {result.Message}");
            MovementDiagnosticMessageText = result.Message;
            if (!result.Accepted)
                MovementDiagnosticLastResultText = result.Message;
            RefreshMovementDiagnosticUi();
        }

        private async Task StopMovementDiagnosticAsync()
        {
            MovementDiagnosticStartResult result =
                await _main.MovementDiagnostic.StopTestAsync("Stop Test button");
            AppendBotLog($"[MovementDiag] {result.Message}");
            MovementDiagnosticMessageText = result.Message;
            RefreshMovementDiagnosticUi();
        }

        private async Task StartMovementDiagnosticObservationAsync()
        {
            MovementDiagnosticStartResult result =
                await _main.MovementDiagnostic.StartPostTestObservationAsync();
            AppendBotLog($"[MovementDiag] {result.Message}");
            MovementDiagnosticMessageText = result.Message;
            RefreshMovementDiagnosticUi();
        }

        private async Task StopMovementDiagnosticObservationAsync()
        {
            MovementDiagnosticStartResult result =
                await _main.MovementDiagnostic.StopObservationAsync("Stop Observation button");
            AppendBotLog($"[MovementDiag] {result.Message}");
            MovementDiagnosticMessageText = result.Message;
            RefreshMovementDiagnosticUi();
        }

        private void MarkMovementRecovery()
        {
            MovementDiagnosticMarkResult result = _main.MovementDiagnostic.MarkRecoveryObserved();
            AppendBotLog($"[MovementDiag] {result.Message}");
            MovementDiagnosticMessageText = result.Message;
            RefreshMovementDiagnosticUi();
        }

        private void RecordMovementDiagnosticObservation()
        {
            MovementRemoteObservationRating rating =
                SelectedMovementDiagnosticObservation?.Rating ?? MovementRemoteObservationRating.NotRecorded;
            MovementDiagnosticStartResult result = _main.MovementDiagnostic.RecordObserverFeedback(
                rating,
                MovementDiagnosticPersistedAfterStop,
                MovementDiagnosticObservedDuringManualMovement,
                MovementDiagnosticNotesText);
            AppendBotLog($"[MovementDiag] {result.Message}");
            MovementDiagnosticMessageText = result.Message;
            RefreshMovementDiagnosticUi();
        }

        private async Task ExportMovementDiagnosticReportAsync()
        {
            string? path = await _main.MovementDiagnostic.ExportReportAsync();
            MovementDiagnosticMessageText = path == null
                ? "Export failed — no diagnostic session exists."
                : $"Report exported to {path}";
            RefreshMovementDiagnosticUi();
        }

        private bool TryBuildMovementDiagnosticSettings(
            out MovementDiagnosticSettings settings,
            out string error)
        {
            settings = new MovementDiagnosticSettings();
            error = "";

            if (!double.TryParse(
                    MovementDiagnosticDurationText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture,
                    out double durationSeconds) &&
                !double.TryParse(
                    MovementDiagnosticDurationText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out durationSeconds))
            {
                error = "Test duration must be a number of seconds (1–300).";
                return false;
            }

            if (durationSeconds < 1 || durationSeconds > 300)
            {
                error = "Test duration must be between 1 and 300 seconds.";
                return false;
            }

            if (!int.TryParse(MovementDiagnosticRepeatIntervalText, out int repeatMs) ||
                repeatMs < 20 || repeatMs > 2000)
            {
                error = "Repeat interval must be between 20 and 2000 ms.";
                return false;
            }

            float? waypointX = ParseOptionalFloat(MovementDiagnosticWaypointXText);
            float? waypointY = ParseOptionalFloat(MovementDiagnosticWaypointYText);
            if (waypointX.HasValue != waypointY.HasValue)
            {
                error = "Configure BOTH safe waypoint X and Y, or leave both empty.";
                return false;
            }

            settings.DurationMs = (int)Math.Round(durationSeconds * 1000.0);
            settings.CountdownMs = 3000;
            settings.RepeatIntervalMs = repeatMs;
            settings.SampleIntervalMs = 100;
            settings.AdvancedModeEnabled = MovementDiagnosticAdvancedMode;
            settings.SafeWaypointX = waypointX;
            settings.SafeWaypointY = waypointY;
            settings.MaxSafeWaypointDistance = 30f;
            settings.AutoStartRecoveryObservation = true;
            settings.RecoveryCheckpointsSeconds = new[] { 15, 30, 60, 120, 300 };
            settings.MaxObservationMinutes = 30;
            return true;
        }

        private static float? ParseOptionalFloat(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out float value) ||
                float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                return value;
            return null;
        }

        private void RefreshMovementDiagnosticUi()
        {
            MovementDiagnosticStatus status = _main.MovementDiagnostic.GetStatus();
            IsMovementDiagnosticBusy = status.IsTestActive;
            IsMovementDiagnosticObservationActive = status.IsObservationActive;
            MovementDiagnosticStateText = status.Phase switch
            {
                MovementDiagnosticPhase.Observing => "Post-test observation",
                _ => status.Phase.ToString()
            };
            MovementDiagnosticMessageText = status.Message;
            MovementDiagnosticElapsedText = FormatDiagnosticTime(status.Elapsed);
            MovementDiagnosticCountdownText = status.CountdownRemaining.HasValue
                ? $"Starting in {Math.Max(0, Math.Ceiling(status.CountdownRemaining.Value.TotalSeconds))}..."
                : "";
            MovementDiagnosticProgressText = status.HoldElapsed.HasValue && status.HoldTarget.HasValue
                ? $"{status.HoldLabel} — {status.HoldElapsed.Value.TotalSeconds:F1} / {status.HoldTarget.Value.TotalSeconds:F1} s"
                : "";
            MovementDiagnosticInputMethodText = status.HasCurrentSession
                ? status.InputMethodDescription
                : MovementDiagnosticSelectedInputMethodText;
            MovementDiagnosticLastResultText = status.LastResult;
            MovementDiagnosticCheckpointText = string.IsNullOrEmpty(status.LastCheckpoint)
                ? ""
                : $"Last checkpoint: {status.LastCheckpoint}";
            MovementDiagnosticReportDirectoryText = status.OutputDirectory ?? "—";

            if (status.RecoveryDurationMs.HasValue)
            {
                MovementDiagnosticRecoveryText = $"Marked: {status.RecoveryDurationMs.Value:F0} ms after confirmed input stop.";
            }
            else if (status.InputInjectionStoppedUtc.HasValue)
            {
                double sinceStop = Math.Max(0, (DateTime.UtcNow - status.InputInjectionStoppedUtc.Value).TotalSeconds);
                MovementDiagnosticRecoveryText = $"Not yet marked ({sinceStop:F0} s since confirmed input stop).";
            }
            else
            {
                MovementDiagnosticRecoveryText = "Unknown (input has not stopped yet).";
            }

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private static string FormatDiagnosticTime(TimeSpan elapsed)
            => $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}";

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

        /// <summary>
        /// Run-duration limit in minutes, typed by the user before pressing Start.
        /// Empty, zero or invalid means the bot runs indefinitely.
        /// </summary>
        public string RunDurationMinutesText
        {
            get => _runDurationMinutesText;
            set => SetProperty(ref _runDurationMinutesText, value);
        }

        /// <summary>Human-readable countdown to the automatic stop ("unlimited" when no timer is armed).</summary>
        public string RunTimeRemainingText
        {
            get => _runTimeRemainingText;
            set => SetProperty(ref _runTimeRemainingText, value);
        }

        /// <summary>Parses <see cref="RunDurationMinutesText"/>; returns 0 when empty/invalid (run indefinitely).</summary>
        private double ParseRunDurationMinutes()
        {
            var t = (RunDurationMinutesText ?? "").Trim();
            if (string.IsNullOrEmpty(t)) return 0;
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double v) ||
                double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                if (v < 0 || double.IsNaN(v) || double.IsInfinity(v)) return 0;
                return v;
            }
            return 0;
        }

        public bool CanStartWorkflow =>
            !string.IsNullOrWhiteSpace(SelectedProfileName);

        // �������������������� Test Method control ��������������������

        /// <summary>
        /// Operation names selectable in the Test Method control. Populated from
        /// the <see cref="BotOperations"/> registry.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<string> AvailableOperationNames =>
            _availableOperationNames;

        public string? SelectedOperationName
        {
            get => _selectedOperationName;
            set
            {
                if (SetProperty(ref _selectedOperationName, value))
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Result text of the last operation test run ("Idle", "Running�", "Success", "Failed", "Error").</summary>
        public string OperationTestStatus
        {
            get => _operationTestStatus;
            set => SetProperty(ref _operationTestStatus, value);
        }

        public bool IsOperationTestRunning
        {
            get => _isOperationTestRunning;
            set
            {
                if (SetProperty(ref _isOperationTestRunning, value))
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Result text of the last manual loot scan test (Tools tab).</summary>
        public string LootScanTestStatus
        {
            get => _lootScanTestStatus;
            set => SetProperty(ref _lootScanTestStatus, value);
        }

        public bool IsLootScanTestRunning
        {
            get => _isLootScanTestRunning;
            set
            {
                if (SetProperty(ref _isLootScanTestRunning, value))
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private void BuildAvailableOperationNames()
        {
            _availableOperationNames.Clear();
            foreach (var name in BotOperations.KnownNames)
                _availableOperationNames.Add(name);

            if (_availableOperationNames.Count > 0)
                SelectedOperationName = _availableOperationNames[0];
        }

        /// <summary>
        /// Runs the selected bot operation once (outside of the workflow) so it can
        /// be tested from the control panel. The result is shown in
        /// <see cref="OperationTestStatus"/> and logged to the bot log.
        /// </summary>
        private async void RunOperationTest()
        {
            if (!_main.IsAttached)
            {
                AppendBotLog("[Operation Test] Attach to the game first — running an operation needs live memory.");
                OperationTestStatus = "Not attached";
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedOperationName))
            {
                AppendBotLog("[Operation Test] No operation selected.");
                OperationTestStatus = "No operation selected";
                return;
            }

            IsOperationTestRunning = true;
            OperationTestStatus = "Running�";
            AppendBotLog($"[Operation Test] Starting '{SelectedOperationName}'...");
            try
            {
                bool ok = await _main.TestOperationAsync(SelectedOperationName);
                OperationTestStatus = ok ? "Success" : "Failed";
            }
            catch (Exception ex)
            {
                OperationTestStatus = "Error";
                AppendBotLog($"[Operation Test] Error: {ex.Message}");
            }
            finally
            {
                IsOperationTestRunning = false;
            }
        }

        /// <summary>
        /// Runs the real loot logic once (camera zoom, client capture, pixel scan,
        /// mouseover probe and pickup) so the operation can be diagnosed from the
        /// Tools tab. The returned status reports whether an item was collected or
        /// the scan simply completed without a match.
        /// </summary>
        private async void RunLootScanTest(LootSystem.LootDiagnosticMode mode)
        {
            if (!_main.IsAttached)
            {
                AppendBotLog("[Loot Test] Attach to the game first — loot diagnostics need live memory.");
                LootScanTestStatus = "Not attached";
                return;
            }

            string label = mode == LootSystem.LootDiagnosticMode.SodSopPink ? "SOD/SOP" : "normal";
            IsLootScanTestRunning = true;
            LootScanTestStatus = "Running...";
            AppendBotLog($"[Loot Test] Starting {label} loot scan...");
            AppendBotLog($"[Loot Test] Screenshots will be saved to: {ScreenshotService.GetDiagnosticLootScanOutputDirectory()}");
            try
            {
                LootSystem.LootDiagnosticResult result = await _main.RunLootDiagnosticScanAsync(mode);
                LootScanTestStatus = DescribeLootScanResult(result);
            }
            catch (Exception ex)
            {
                LootScanTestStatus = "Error";
                AppendBotLog($"[Loot Test] Error: {ex.Message}");
            }
            finally
            {
                IsLootScanTestRunning = false;
            }
        }

        private static string DescribeLootScanResult(LootSystem.LootDiagnosticResult result)
        {
            string label = result.Mode == LootSystem.LootDiagnosticMode.SodSopPink ? "SOD/SOP" : "Normal";
            return result.Outcome switch
            {
                LootSystem.LootDiagnosticOutcome.Collected => $"{label}: collected {result.ItemsCollected}",
                LootSystem.LootDiagnosticOutcome.NoItem => $"{label}: no item found",
                LootSystem.LootDiagnosticOutcome.CaptureFailed => $"{label}: capture failed",
                LootSystem.LootDiagnosticOutcome.Cancelled => $"{label}: cancelled",
                LootSystem.LootDiagnosticOutcome.NotAttached => $"{label}: not attached",
                LootSystem.LootDiagnosticOutcome.NotFocused => $"{label}: game not focused",
                LootSystem.LootDiagnosticOutcome.InCity => $"{label}: in city",
                LootSystem.LootDiagnosticOutcome.Busy => $"{label}: busy",
                _ => $"{label}: error"
            };
        }

        private void RunBot()
        {
            if (!_main.IsAttached)
            {
                AppendBotLog("Attach to the game before starting the manual bot.");
                return;
            }

            _main.RunBot();
            double minutes = ParseRunDurationMinutes();
            if (minutes > 0)
                _main.StartAutoStopTimer(minutes, "Bot");
        }

        private void StopAllBots()
        {
            _main.StopAllActions("manual stop");
        }

        private void ToggleHealManaBot()
        {
            if (!_main.IsAttached && !IsHealManaBotRunning)
            {
                AppendBotLog("Attach to the game before starting the heal/mana bot.");
                return;
            }

            _main.ToggleHealManaBotInternal();
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
                AppendBotLog($"Profile '{profile.Name}' is valid.");
            }
            else
            {
                ValidationText = $"Profile '{profile.Name}' validation ERRORS:";
                AppendBotLog($"Profile '{profile.Name}' validation ERRORS:");
                foreach (var err in errors)
                {
                    ValidationText += "\n" + err;
                    AppendBotLog($"  - {err}");
                }
            }
        }

        /// <summary>
        /// Starts the route workflow for the selected profile: requires a selection,
        /// loads and validates the profile, starts the workflow and logs the three stages.
        /// </summary>
        private void StartWorkflowWithProfile()
        {
            if (!_main.IsAttached)
            {
                AppendBotLog("Attach to the game before starting the workflow.");
                ValidationText = "Not attached to the game — attach first, then start the workflow. Profile editing and validation still work offline.";
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedProfileName))
            {
                AppendBotLog("No profile selected. Workflow cannot start.");
                ValidationText = "No profile selected.";
                return;
            }

            var profile = _main.LoadProfile(SelectedProfileName);
            if (profile == null)
            {
                AppendBotLog($"Failed to load profile '{SelectedProfileName}'.");
                ValidationText = $"Failed to load profile '{SelectedProfileName}'.";
                return;
            }

            // Validate before starting
            var errors = _main.ValidateProfile(profile);
            if (errors.Count > 0)
            {
                AppendBotLog("Profile validation failed:");
                foreach (var error in errors)
                    AppendBotLog(" - " + error);
                AppendBotLog("Workflow NOT started. Fix profile errors first.");
                ValidationText = "Validation FAILED � check main log.";
                return;
            }

            _main.StartWorkflow(profile);

            double runMinutes = ParseRunDurationMinutes();
            if (runMinutes > 0 && _main.IsAttached)
                _main.StartAutoStopTimer(runMinutes, "Workflow");

            AppendBotLog($"Workflow started with profile '{profile.Name}'.");

            var flowSteps = profile.FlowSteps ?? new List<BotFlowStep>();
            if (flowSteps.Count == 0)
            {
                AppendBotLog("  Flow: no steps configured.");
            }
            else
            {
                AppendBotLog($"  Flow ({flowSteps.Count} steps):");
                for (int i = 0; i < flowSteps.Count; i++)
                {
                    var step = flowSteps[i];
                    if (step == null) continue;
                    AppendBotLog($"    {i + 1}. {DescribeFlowStepForLog(step)}");
                }
            }
        }

        private static string DescribeFlowStepForLog(BotFlowStep step)
        {
            switch (step.Type)
            {
                case BotFlowStepType.Path:
                    string mode = step.CompletionMode == TravelRouteCompletionMode.ExpectedMapReached
                        ? $"finish when destination map loaded � map {step.ExpectedDestinationMapNumber}"
                        : "finish when last waypoint reached";
                    if (step.Routes != null && step.Routes.Count > 0)
                        return $"Path group ({step.Routes.Count} routes, wait {step.StartDelayMs} ms, {mode})";
                    return $"Path '{step.PathFile}' (wait {step.StartDelayMs} ms, {mode})";
                case BotFlowStepType.Repot:
                    int count = step.RepotPaths?.Count ?? 0;
                    var names = (step.RepotPaths ?? new List<BotRouteStep>())
                        .Where(p => p != null && !string.IsNullOrWhiteSpace(p.PathFile))
                        .Select(p => p.PathFile);
                    return $"Repot (paths: {string.Join(", ", names)})";
                case BotFlowStepType.Operation:
                    return $"Operation '{step.OperationName}'";
                case BotFlowStepType.ExpLoop:
                    if (step.Routes != null && step.Routes.Count > 0)
                        return $"ExpLoop group ({step.Routes.Count} routes, wait {step.StartDelayMs} ms)";
                    return $"ExpLoop '{step.PathFile}' (wait {step.StartDelayMs} ms)";
                default:
                    return step.Type.ToString();
            }
        }

        public void SyncBotStates()
        {
            IsMovementBotRunning = _main.IsMovementBotRunningInternal;
            IsHealManaBotRunning = _main.IsHealManaBotRunningInternal;
            IsLootBotRunning = _main.IsLootBotRunningInternal;
            IsBotPaused = _main.IsBotPaused;
            IsGameAttached = _main.IsAttached;
        }

        private void RefreshStats()
        {
            if (System.Threading.Volatile.Read(ref _isDisposed) != 0)
                return;

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (System.Threading.Volatile.Read(ref _isDisposed) != 0)
                    return;

                // Attachment / workflow / pause state is synced regardless of attachment
                // so stopping or pausing a bot always works, even if the game went away.
                IsGameAttached = _main.IsAttached;
                IsMovementBotRunning = _main.IsMovementBotRunningInternal;
                IsHealManaBotRunning = _main.IsHealManaBotRunningInternal;
                IsLootBotRunning = _main.IsLootBotRunningInternal;
                IsBotPaused = _main.IsBotPaused;

                OnPropertyChanged(nameof(MobGroupingStatus));

                // Movement synchronization diagnostic state (may run on a worker thread).
                RefreshMovementDiagnosticUi();

                // Sync workflow state from MainViewModel
                IsWorkflowRunning = _main.IsWorkflowRunning;
                WorkflowPhaseText = _main.WorkflowPhaseText;
                OnPropertyChanged(nameof(WorkflowCycleCount));

                // Run-duration countdown (updated every second with the stats poll).
                var remaining = _main.GetAutoStopRemaining();
                RunTimeRemainingText = remaining == null
                    ? "unlimited"
                    : remaining.Value.TotalHours >= 1
                        ? $"{(int)remaining.Value.TotalHours}:{remaining.Value.Minutes:D2}:{remaining.Value.Seconds:D2} left"
                        : $"{remaining.Value.Minutes:D2}:{remaining.Value.Seconds:D2} left";

                // Live player stats need the game; everything above does not.
                if (!_main.IsAttached)
                {
                    CurrentHp = "--";
                    CurrentMana = "--";
                    HpPotCount = "--";
                    ManaPotCount = "--";
                    return;
                }

                // Thresholds are user-owned configuration and are intentionally not
                // synchronized by this periodic runtime-stats refresh.
                var (hp, mana, success) = _main.GetHpMana();
                if (success)
                {
                    CurrentHp = hp.ToString();
                    CurrentMana = mana.ToString();
                }
                HpPotCount = _main.GetHpPotionCount().ToString();
                ManaPotCount = _main.GetManaPotionCount().ToString();
            });
        }

        /// <summary>
        /// Seeds the UI log with the shared history so opening the Bot panel later still
        /// shows the lines produced while it was closed (or while detached).
        /// </summary>
        private void SeedBotLogFromService()
        {
            lock (_botLogLock)
            {
                _botLogLines.Clear();
                foreach (var line in BotLogService.Instance.Snapshot())
                    _botLogLines.Enqueue(line);
                BotLogText = _botLogLines.Count == 0 ? "" : string.Join("\r\n", _botLogLines) + "\r\n";
            }
        }

        private void OnBotLogLine(string stamped)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => AppendBotLogLineToUi(stamped)));
                return;
            }

            AppendBotLogLineToUi(stamped);
        }

        private void AppendBotLogLineToUi(string stamped)
        {
            if (System.Threading.Volatile.Read(ref _isDisposed) != 0)
                return;

            lock (_botLogLock)
            {
                _botLogLines.Enqueue(stamped);
                while (_botLogLines.Count > BotLogService.MaxLines)
                    _botLogLines.Dequeue();
                BotLogText = string.Join("\r\n", _botLogLines) + "\r\n";
            }
        }

        private void OnMainAttachmentChanged(bool attached)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => OnMainAttachmentChanged(attached)));
                return;
            }

            IsGameAttached = attached;
        }

        /// <summary>
        /// Appends a line to the shared bot log (UI history + unlimited file log). Safe to
        /// call from any thread; the Bot window subscribes and marshals to the UI thread.
        /// </summary>
        public void AppendBotLog(string line)
        {
            BotLogService.Instance.Append(line);
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            // Closing the Bot Control Panel (which hosts the diagnostic section) must
            // stop the active experiment. This is a bounded cleanup; failures are logged.
            try { _main.MovementDiagnostic.StopForWindowClose(); }
            catch (Exception ex) { AppendBotLog($"[MovementDiag] Window-close cleanup error: {ex.Message}"); }

            BotLogService.Instance.LineAppended -= OnBotLogLine;
            _main.AttachmentChanged -= OnMainAttachmentChanged;
            _main.PropertyChanged -= OnMainPropertyChanged;

            System.Threading.Interlocked.Exchange(ref _statsTimer, null)?.Dispose();
        }

        private void ClearBotLog()
        {
            BotLogService.Instance.Clear();
            lock (_botLogLock)
            {
                _botLogLines.Clear();
                BotLogText = "";
            }
        }

        private void PauseBots()
        {
            _main.PauseAllBots();
            IsBotPaused = _main.IsBotPaused;
        }

        private void ResumeBots()
        {
            _main.ResumeAllBots();
            IsBotPaused = _main.IsBotPaused;
        }
    }
}
