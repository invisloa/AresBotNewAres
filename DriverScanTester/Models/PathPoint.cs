using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using DriverScanTester.Services;
using DriverScanTester.Utils;

namespace DriverScanTester.Models
{
    public class PathPoint : INotifyPropertyChanged
    {
        public const short DefaultCameraDistanceLock = BotConstants.Camera.DefaultDistanceLock;
        public const short DefaultAttackDisengageDistance = BotConstants.Combat.DefaultAttackDisengageDistance;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private float _x;
        public float X { get => _x; set => SetField(ref _x, value); }

        private float _y;
        public float Y { get => _y; set => SetField(ref _y, value); }

        private MovementPrecision _precision = MovementPrecision.Medium;
        public MovementPrecision Precision { get => _precision; set => SetField(ref _precision, value); }

        private BotMode _mode = BotMode.OnlyMove;
        public BotMode Mode { get => _mode; set => SetField(ref _mode, value); }

        private MovementSteeringMode? _steeringMode;
        /// <summary>
        /// Per-point approach steering override: KeyboardTurn = WAD keys (A/D turn while W drives),
        /// DirectCamera = write the camera angle directly. Null = inherit the path runner default
        /// (KeyboardTurn / WAD for normal routes). New points default to WAD in the editor.
        /// </summary>
        public MovementSteeringMode? SteeringMode { get => _steeringMode; set => SetField(ref _steeringMode, value); }

        private int? _startCameraAngleBits;
        /// <summary>
        /// Optional horizontal camera yaw (left/right camera) applied once when the bot
        /// starts moving toward this point, while W is released and before the leg's
        /// first W-down. The value is the exact 32-bit float bit pattern written at
        /// [Ares.exe + 0x4883F0] + 0x1A8, so a captured camera angle round-trips
        /// without precision loss. Null = leave the camera untouched. The first point
        /// therefore controls the camera at the start of the whole path.
        /// </summary>
        public int? StartCameraAngleBits
        {
            get => _startCameraAngleBits;
            set
            {
                if (SetField(ref _startCameraAngleBits, value))
                {
                    OnPropertyChanged(nameof(StartCameraAngleText));
                    OnPropertyChanged(nameof(StartCameraAngleDescription));
                }
            }
        }

        /// <summary>
        /// Editor text for <see cref="StartCameraAngleBits"/>. Accepts blank (none),
        /// radians (1.5736735 / 1.5736735rad), degrees (90deg / 90°) or a raw bit
        /// pattern (0x3FC96E22 / 1070165538). Not persisted separately.
        /// </summary>
        [JsonIgnore]
        public string StartCameraAngleText
        {
            get => StartCameraAngleBits.HasValue
                ? CameraAngleValue.Format(StartCameraAngleBits.Value)
                : "";
            set
            {
                if (CameraAngleValue.TryParse(value, out int? bits))
                    StartCameraAngleBits = bits;
                else
                    OnPropertyChanged(); // reject invalid text, keep the last valid angle
            }
        }

        /// <summary>Read-only decode of the configured angle (raw bits / radians / degrees).</summary>
        [JsonIgnore]
        public string StartCameraAngleDescription =>
            CameraAngleValue.Describe(StartCameraAngleBits);

        private short _cameraDistanceLock = DefaultCameraDistanceLock;
        public short CameraDistanceLock { get => _cameraDistanceLock; set => SetField(ref _cameraDistanceLock, value); }

        private short _attackDisengageDistance = DefaultAttackDisengageDistance;
        public short AttackDisengageDistance { get => _attackDisengageDistance; set => SetField(ref _attackDisengageDistance, value); }

        private ZoneRestriction _zoneRestriction = ZoneRestriction.OutsideOnly;
        public ZoneRestriction ZoneRestriction { get => _zoneRestriction; set => SetField(ref _zoneRestriction, value); }

        private WaypointStuckRecoveryType _stuckRecoveryType = WaypointStuckRecoveryType.Default;
        public WaypointStuckRecoveryType StuckRecoveryType
        {
            get => _stuckRecoveryType;
            set => SetField(ref _stuckRecoveryType, value);
        }

        private string _stuckRecoveryOperation = "";
        public string StuckRecoveryOperation { get => _stuckRecoveryOperation; set => SetField(ref _stuckRecoveryOperation, value); }

        private string _stuckRecoveryPath = "";
        public string StuckRecoveryPath { get => _stuckRecoveryPath; set => SetField(ref _stuckRecoveryPath, value); }

        private short _stuckRecoveryMobCameraDistance = DefaultCameraDistanceLock;
        public short StuckRecoveryMobCameraDistance
        {
            get => _stuckRecoveryMobCameraDistance;
            set => SetField(ref _stuckRecoveryMobCameraDistance, value);
        }

        private string _onArrivalOperation = "";
        /// <summary>
        /// Named bot operation executed once when this point is reached, before the
        /// route continues. Empty = none. Stored in the saved path file, so methods
        /// like EtanaRepotUnstuck can be attached to the path itself, not only as
        /// profile flow steps.
        /// </summary>
        public string OnArrivalOperation { get => _onArrivalOperation; set => SetField(ref _onArrivalOperation, value); }

        private bool _isOperationStep;
        /// <summary>
        /// When true, this row is a standalone method step: it runs in path order
        /// regardless of the player's current position (no distance check) and X/Y
        /// plus movement fields are ignored. This is how a bare method becomes the
        /// 1st (or any) point of a path.
        /// </summary>
        public bool IsOperationStep { get => _isOperationStep; set => SetField(ref _isOperationStep, value); }

        public PathPoint() { }
        public PathPoint(
            float x,
            float y,
            MovementPrecision precision = MovementPrecision.Medium,
            BotMode mode = BotMode.OnlyMove,
            short cameraDistanceLock = DefaultCameraDistanceLock,
            short attackDisengageDistance = DefaultAttackDisengageDistance,
            ZoneRestriction zoneRestriction = ZoneRestriction.OutsideOnly,
            WaypointStuckRecoveryType stuckRecoveryType = WaypointStuckRecoveryType.Default,
            string stuckRecoveryOperation = "",
            string stuckRecoveryPath = "",
            short stuckRecoveryMobCameraDistance = DefaultCameraDistanceLock,
            string onArrivalOperation = "",
            bool isOperationStep = false,
            MovementSteeringMode? steeringMode = null,
            int? startCameraAngleBits = null)
        {
            _x = x;
            _y = y;
            _precision = precision;
            _mode = mode;
            _cameraDistanceLock = cameraDistanceLock;
            _attackDisengageDistance = attackDisengageDistance;
            _zoneRestriction = zoneRestriction;
            _stuckRecoveryType = stuckRecoveryType;
            _stuckRecoveryOperation = stuckRecoveryOperation;
            _stuckRecoveryPath = stuckRecoveryPath;
            _stuckRecoveryMobCameraDistance = stuckRecoveryMobCameraDistance;
            _onArrivalOperation = onArrivalOperation;
            _isOperationStep = isOperationStep;
            _steeringMode = steeringMode;
            _startCameraAngleBits = startCameraAngleBits;
        }
    }
}
