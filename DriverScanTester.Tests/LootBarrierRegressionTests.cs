using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using DriverScanTester.Utils;
using static DriverScanTester.Services.LootSystem;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the corrected MoveAndAttackAndLoot behavior:
    /// ordinary waypoint movement never arms a stationary loot scan; the passive
    /// moving pre-scan only observes the current view; a confirmed post-kill request
    /// is the only mandatory full loot cycle.
    /// </summary>
    public sealed class LootBarrierRegressionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Color AquamarineLoot = Color.FromArgb(0, 255, 170);

        private sealed class RetargetMemory
        {
            private const ulong ModuleBase = 0x100000;
            private const ulong PlayerBase = 0x200000;
            private const ulong CameraBase = 0x300000;

            public short X = 0;
            public short Y = 0;
            public int Map = 1;
            public int TargetId;
            public byte Action = 25;
            public short CameraDistance = 17020;
            public bool IgnoreCameraWrites;
            public Action? AfterCameraWrite;

            public GameMemoryService Create(Action<string> log)
                => new(1, Read, Write, ModuleBase, 8, log);

            private bool Read(uint pid, ulong address, byte[] buffer, out uint bytesRead)
            {
                byte[] value;
                if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                    value = BitConverter.GetBytes(PlayerBase);
                else if (address == ModuleBase + BotConstants.MemoryOffsets.CameraPtr)
                    value = BitConverter.GetBytes(CameraBase);
                else if (address >= PlayerBase && address < PlayerBase + 0x5000)
                {
                    ulong offset = address - PlayerBase;
                    value = offset switch
                    {
                        BotConstants.MemoryOffsets.X => BitConverter.GetBytes(X),
                        BotConstants.MemoryOffsets.Y => BitConverter.GetBytes(Y),
                        BotConstants.MemoryOffsets.CurrentMap => BitConverter.GetBytes(Map),
                        BotConstants.MemoryOffsets.TargetSelected => BitConverter.GetBytes(TargetId),
                        BotConstants.MemoryOffsets.CurrentAction => new[] { Action },
                        BotConstants.MemoryOffsets.IsInCity => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.AttackSpeed1 => BitConverter.GetBytes((short)0),
                        BotConstants.MemoryOffsets.Mana => BitConverter.GetBytes((short)1000),
                        _ => Array.Empty<byte>()
                    };
                }
                else if (address >= CameraBase && address < CameraBase + 0x1000)
                {
                    ulong offset = address - CameraBase;
                    value = offset switch
                    {
                        BotConstants.MemoryOffsets.CameraDistance => BitConverter.GetBytes(CameraDistance),
                        BotConstants.MemoryOffsets.CameraAngle => BitConverter.GetBytes(0f),
                        _ => Array.Empty<byte>()
                    };
                }
                else
                    value = Array.Empty<byte>();

                bytesRead = 0;
                if (value.Length < buffer.Length)
                    return false;
                Buffer.BlockCopy(value, 0, buffer, 0, buffer.Length);
                bytesRead = (uint)buffer.Length;
                return true;
            }

            private bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = (uint)buffer.Length;
                if (address == CameraBase + BotConstants.MemoryOffsets.CameraDistance && buffer.Length == 2)
                {
                    if (!IgnoreCameraWrites)
                        CameraDistance = BitConverter.ToInt16(buffer, 0);
                    AfterCameraWrite?.Invoke();
                }
                return true;
            }
        }

        private sealed class RetargetMovement : MovementSystem
        {
            private readonly RetargetMemory _memory;
            private int _cursorX;
            private int _cursorY;
            private bool _cursorKnown;

            public RetargetMovement(
                GameMemoryService gameMemory,
                Action<string> log,
                RetargetMemory memory,
                StubCombatPlayerPresenceScanner presence,
                BotMode mode = BotMode.MoveAndAttackAndLoot)
                : base(gameMemory, log, 10, 0,
                    customPath: new[]
                    {
                        new Waypoint(10, 0, MovementPrecision.Medium, mode, cameraDistanceLock: 17020)
                    },
                    initialMode: mode,
                    loopPath: false,
                    steeringMode: MovementSteeringMode.DirectCamera,
                    targetSafety: CombatSafetyFixtures.WithPresence(() => memory.TargetId, presence))
            {
                _memory = memory;
                InternalRepotEnabled = false;
            }

            public int TargetCyclePresses { get; private set; }
            public List<short> TargetCycleCameraDistances { get; } = new();
            public Action? OnTargetCycle { get; set; }
            public bool Foreground { get; set; } = true;
            public bool CursorMoveReachesTarget { get; set; } = true;
            public bool ReleaseTargetOnLeftUp { get; set; } = true;
            public List<(int X, int Y)> CursorMoves { get; } = new();
            public List<MouseOperations.MouseEventFlags> MouseEvents { get; } = new();
            public int ForwardKeyDowns { get; private set; }
            public int SkillThreeDowns { get; private set; }
            public List<string> InputOrder { get; } = new();

            protected override void SendTargetCycleKey()
            {
                InputOrder.Add("TAB");
                TargetCyclePresses++;
                TargetCycleCameraDistances.Add(_memory.CameraDistance);
                OnTargetCycle?.Invoke();
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
            {
                if (!keyUp)
                    ForwardKeyDowns++;
            }

            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }
            protected override void SendTurnKeyEvent(bool leftA, bool keyUp) { }
            protected override void SendSkillThreeKeyEvent(bool keyUp)
            {
                if (!keyUp)
                {
                    InputOrder.Add("Skill3Down");
                    SkillThreeDowns++;
                }
            }

            protected override bool TryResolveCombatGateClientArea(
                out nint hwnd, out int clientScreenLeft, out int clientScreenTop,
                out int clientWidth, out int clientHeight)
            {
                hwnd = Foreground ? (nint)1 : nint.Zero;
                clientScreenLeft = 448;
                clientScreenTop = 131;
                clientWidth = 1024;
                clientHeight = 768;
                return Foreground;
            }

            protected override bool IsCombatGateClientForeground(nint hwnd)
                => Foreground && hwnd != nint.Zero;

            protected override void MoveCombatGateMouseCursor(int screenX, int screenY)
            {
                CursorMoves.Add((screenX, screenY));
                if (CursorMoveReachesTarget)
                {
                    _cursorX = screenX;
                    _cursorY = screenY;
                    _cursorKnown = true;
                }
            }

            protected override bool TryGetCombatGateCursorPosition(out int screenX, out int screenY)
            {
                screenX = _cursorX;
                screenY = _cursorY;
                return _cursorKnown;
            }

            protected override void SendCombatGateMouseEvent(MouseOperations.MouseEventFlags flags)
            {
                InputOrder.Add($"Mouse:{flags}");
                MouseEvents.Add(flags);
                if (flags == MouseOperations.MouseEventFlags.LeftUp && ReleaseTargetOnLeftUp)
                    _memory.TargetId = 0;
            }
        }

        private sealed class RetargetScenario : IDisposable
        {
            public RetargetMemory Memory { get; } = new();
            public List<string> Log { get; } = new();
            public StubCombatPlayerPresenceScanner Presence { get; } = new();
            public GameMemoryService GameMemory { get; }
            public LootSystem Loot { get; }
            public RetargetMovement Movement { get; }

            public RetargetScenario(BotMode mode = BotMode.MoveAndAttackAndLoot)
            {
                GameMemory = Memory.Create(Log.Add);
                Loot = new LootSystem(GameMemory, Log.Add);
                Movement = new RetargetMovement(GameMemory, Log.Add, Memory, Presence, mode)
                {
                    LootSystemRef = Loot
                };
            }

            public void Dispose()
            {
                Movement.Stop();
                Loot.Dispose();
            }
        }

        private static RetargetScenario CreateRetargetScenario(BotMode mode = BotMode.MoveAndAttackAndLoot)
            => new(mode);

        private static async Task<bool> InvokePrivateBoolAsync(
            object target, string method, params object[] args)
        {
            MethodInfo? info = FindPrivateMethod(target.GetType(), method);
            Assert.NotNull(info);
            var task = info!.Invoke(target, args) as Task<bool>;
            Assert.NotNull(task);
            return await task!;
        }

        private static MethodInfo? FindPrivateMethod(Type type, string method)
        {
            for (Type? current = type; current != null; current = current.BaseType)
            {
                MethodInfo? found = current.GetMethod(method,
                    PrivateInstance | BindingFlags.DeclaredOnly);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            for (Type? current = target.GetType(); current != null; current = current.BaseType)
            {
                FieldInfo? field = current.GetField(fieldName,
                    PrivateInstance | BindingFlags.DeclaredOnly);
                if (field == null)
                    continue;
                field.SetValue(target, value);
                return;
            }
            Assert.Fail($"Private field '{fieldName}' was not found.");
        }

        private static T ReadPrivateField<T>(object target, string fieldName)
        {
            for (Type? current = target.GetType(); current != null; current = current.BaseType)
            {
                FieldInfo? field = current.GetField(fieldName,
                    PrivateInstance | BindingFlags.DeclaredOnly);
                if (field == null)
                    continue;
                return (T)field.GetValue(target)!;
            }
            Assert.Fail($"Private field '{fieldName}' was not found.");
            return default!;
        }

        private static void SetLootRequestProperty(
            LootSystem loot, long requestId, string propertyName, object value)
        {
            var history = typeof(LootSystem)
                .GetField("_mandatoryLootRequestHistory", PrivateInstance)!
                .GetValue(loot)!;
            var dictionary = (System.Collections.IDictionary)history;
            object? request = dictionary[requestId];
            Assert.NotNull(request);
            PropertyInfo? property = request!.GetType().GetProperty(propertyName);
            Assert.NotNull(property);
            property!.SetValue(request, value);
        }

        /// <summary>
        /// Drives a real mandatory request into the requested lifecycle state so the
        /// post-kill retarget guard is exercised against the authoritative LootSystem
        /// snapshot (no string/source inspection).
        /// </summary>
        private static void DriveMandatoryRequestState(
            RetargetScenario scenario, long requestId, LootRequestState state)
        {
            switch (state)
            {
                case LootRequestState.Pending:
                    break;
                case LootRequestState.Running:
                    scenario.Loot.NotifyNavigationHeld(requestId);
                    RunMandatoryService(scenario.Loot);
                    break;
                case LootRequestState.Deferred:
                    scenario.Loot.NotifyNavigationHeld(requestId);
                    RunMandatoryService(scenario.Loot, mobSelected: true);
                    break;
                case LootRequestState.Completed:
                    scenario.Loot.NotifyNavigationHeld(requestId);
                    RunMandatoryService(scenario.Loot);
                    CompleteMandatory(scenario.Loot, requestId, ValidEmptyOutcome());
                    // A completion whose camera was not verified must never count as success.
                    SetLootRequestProperty(scenario.Loot, requestId, "CameraVerified", false);
                    break;
                case LootRequestState.Failed:
                    scenario.Loot.NotifyNavigationHeld(requestId);
                    RunMandatoryService(scenario.Loot);
                    InvokePrivate(scenario.Loot, "FailMandatoryLootRequest", requestId, "test failure");
                    break;
                case LootRequestState.Cancelled:
                    scenario.Loot.CancelMandatoryLootRequest(requestId, "test cancellation");
                    break;
                case LootRequestState.Unavailable:
                    // Post-kill requests never use Unavailable in production; force it to
                    // prove the guard still refuses to treat it as a finished scan.
                    SetLootRequestProperty(scenario.Loot, requestId, "State", LootRequestState.Unavailable);
                    SetLootRequestProperty(scenario.Loot, requestId, "Reason", LootBarrierReason.PostKill);
                    SetPrivateField(scenario.Loot, "_activeMandatoryLootRequest", null!);
                    break;
                default:
                    Assert.Fail($"Unhandled request state {state}.");
                    break;
            }
        }

        private static GameMemoryService CreateStubMemory(Action<string> log)
        {
            GameMemoryService.ReadMemoryDelegate read =
                (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                {
                    bytesRead = 0;
                    return false;
                };
            GameMemoryService.WriteMemoryDelegate write =
                (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                {
                    bytesWritten = 0;
                    return false;
                };
            return new GameMemoryService(0, read, write, 0, 8, log);
        }

        private static GameMemoryService CreateIdentityMemory(
            uint pid, ulong moduleBase, Action<string> log)
        {
            GameMemoryService.ReadMemoryDelegate read =
                (uint readPid, ulong address, byte[] buffer, out uint bytesRead) =>
                {
                    bytesRead = 0;
                    return false;
                };
            GameMemoryService.WriteMemoryDelegate write =
                (uint writePid, ulong address, byte[] buffer, out uint bytesWritten) =>
                {
                    bytesWritten = 0;
                    return false;
                };
            return new GameMemoryService(pid, read, write, moduleBase, 8, log);
        }

        private static LootSystem CreateLoot(Action<string> log)
            => new LootSystem(CreateStubMemory(log), log);

        private static MovementSystem CreateMovement(
            Action<string> log,
            BotMode mode = BotMode.MoveAndAttackAndLoot,
            LootSystem? loot = null,
            int waypointCount = 1,
            bool loopPath = false)
        {
            var path = new List<Waypoint>();
            for (int i = 0; i < waypointCount; i++)
                path.Add(new Waypoint(1000 * (i + 1), 1000 * (i + 1), MovementPrecision.Medium, mode));

            var movement = new MovementSystem(
                CreateStubMemory(log),
                log,
                1000,
                1000,
                customPath: path,
                loopPath: loopPath)
            {
                InternalRepotEnabled = false,
                LootSystemRef = loot
            };
            return movement;
        }

        private static LootRequestStatus Snapshot(
            long requestId,
            LootBarrierReason reason,
            LootRequestState state,
            bool cameraVerified = true,
            int itemsCollected = 0,
            string detail = "test")
            => new(requestId, reason, state, cameraVerified,
                itemsCollected, detail, DateTime.UtcNow);

        private static int QueueCount(MovementSystem movement)
        {
            var field = typeof(MovementSystem).GetField("_waypoints", PrivateInstance);
            Assert.NotNull(field);
            var queue = field!.GetValue(movement) as Queue<Waypoint>;
            Assert.NotNull(queue);
            return queue!.Count;
        }

        private static bool IsMovingForward(MovementSystem movement)
        {
            var field = typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance);
            Assert.NotNull(field);
            return (bool)field!.GetValue(movement)!;
        }

        private static void SetMovingForward(MovementSystem movement, bool value)
        {
            var field = typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance);
            Assert.NotNull(field);
            field!.SetValue(movement, value);
        }

        private static bool BarrierHoldsInput(MovementSystem movement)
        {
            var field = typeof(MovementSystem).GetField("_lootNavigationBarrierHoldsInput", PrivateInstance);
            Assert.NotNull(field);
            return (bool)field!.GetValue(movement)!;
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            MethodInfo? info = FindPrivateMethod(target.GetType(), method);
            Assert.NotNull(info);
            info!.Invoke(target, args);
        }

        private static async Task<bool> InvokeAdvanceAsync(
            MovementSystem movement,
            float x,
            float y,
            CancellationToken token = default)
        {
            var info = typeof(MovementSystem).GetMethod("AdvanceReachedWaypoints", PrivateInstance);
            Assert.NotNull(info);
            var task = (Task<bool>?)info!.Invoke(movement, new object[] { x, y, token });
            Assert.NotNull(task);
            return await task!;
        }

        private static LootScanPassOutcome ValidEmptyOutcome()
            => new(LootScanPassResult.NoItem, LootScanPassResult.NoItem,
                LootScanPassResult.NoItem, CameraVerified: true, "test empty pass");

        private static bool IsLootStateIdle(LootSystem loot)
        {
            var field = typeof(LootSystem).GetField("_lootState", PrivateInstance);
            Assert.NotNull(field);
            return field!.GetValue(loot)!.ToString() == "Idle";
        }

        private static void RunMandatoryService(LootSystem loot, bool mobSelected = false)
            => InvokePrivate(loot, "ServiceMandatoryLootRequest", mobSelected);

        private static void CompleteMandatory(
            LootSystem loot, long requestId, LootScanPassOutcome outcome)
            => InvokePrivate(loot, "CompleteMandatoryLootRequest", requestId, outcome);

        // ── Test 1 — continuous walking without loot ───────────────────────────

        [Fact]
        public async Task MultipleLootWaypointsAdvanceWithoutAnyLootScanOrStop()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot, waypointCount: 3);
            SetMovingForward(movement, true);

            int index = 0;
            foreach (int coord in new[] { 1000, 2000, 3000 })
            {
                bool advanced = await InvokeAdvanceAsync(movement, coord, coord);
                Assert.True(advanced, $"Waypoint {coord} should advance normally.");
                Assert.False(movement.IsLootNavigationBlocked);
                // The final waypoint legitimately enters standby (existing behavior),
                // but no intermediate transition may stop navigation for loot.
                if (index++ < 2)
                    Assert.True(IsMovingForward(movement), "Ordinary advancement must not release W.");
            }

            Assert.Equal(0, QueueCount(movement));
            Assert.DoesNotContain(lines, line => line.Contains("LootBarrier] Armed", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("LootVerify]", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("LootCamera]", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("StartMoving", StringComparison.Ordinal));
        }

        // ── Test 2 — long journey without loot ─────────────────────────────────

        [Fact]
        public void RepeatedTicksInLootModeNeverStartACycleWithoutARequest()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            loot.MoveAndAttackAndLootMode = true;

            for (int i = 0; i < 100; i++)
            {
                RunMandatoryService(loot);
                Assert.False(loot.IsLootCycleActive,
                    "Idle MoveAndAttackAndLoot must not start the legacy timer-driven collector.");
                Assert.False(loot.HasActiveMandatoryLootRequest);
            }

            Assert.DoesNotContain(lines, line => line.Contains("Spacebar", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("PixelScan started", StringComparison.Ordinal));
        }

        // ── Test 3 — passive candidate scan ────────────────────────────────────

        [Fact]
        public void SpacebarLootIsDisabledByDefault()
        {
            Assert.False(BotConstants.Loot.SpacebarLootEnabledDefault);
        }

        [Fact]
        public void DisabledSpacebarLootNeverStartsTheScanSpamTask()
        {
            bool original = LootRuntimeSettings.SpacebarLootEnabled;
            try
            {
                LootRuntimeSettings.SpacebarLootEnabled = false;
                using var loot = CreateLoot(_ => { });

                InvokePrivate(loot, "StartScanSpacebarSpam");

                var field = typeof(LootSystem).GetField("_scanSpacebarCts", PrivateInstance);
                Assert.NotNull(field);
                Assert.Null(field!.GetValue(loot));
            }
            finally
            {
                LootRuntimeSettings.SpacebarLootEnabled = original;
            }
        }

        [Fact]
        public void PassiveAnalysisFindsPlausibleAquamarineLootWithoutMovingAnything()
        {
            using var bitmap = new Bitmap(200, 200);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(AquamarineLoot))
            {
                graphics.Clear(Color.Black);
                graphics.FillRectangle(brush, 50, 50, 14, 14);
            }

            LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                bitmap, new Rectangle(0, 0, 200, 200),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.PotentialCandidate, result.Outcome);
            Assert.True(result.CandidateComponents >= 1);
            Assert.True(result.LootPixels > 0);
            Assert.False(result.CandidateBounds.IsEmpty);
            Assert.Equal((short)17050, BotConstants.Camera.LootScanDistance);
            Assert.Equal(BotConstants.Camera.LootScanDistance, LootSystem.NormalLootScanDistance);
        }

        [Fact]
        public void TravelCandidateRequestStopsNavigationAndStartsVerificationOnce()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);
            SetMovingForward(movement, true);

            long requestId = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            Assert.True(requestId > 0);

            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.True(movement.IsLootNavigationBlocked);
            Assert.Equal(LootBarrierReason.TravelCandidateVerification, movement.LootBarrier.Reason);
            Assert.True(BarrierHoldsInput(movement));

            InvokePrivate(movement, "StartMoving");
            Assert.False(IsMovingForward(movement));

            loot.NotifyNavigationHeld(requestId);
            RunMandatoryService(loot);
            Assert.True(loot.IsLootCycleActive);
            Assert.True(loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Running, status.State);
        }

        // ── Test 4 — false-positive candidate ──────────────────────────────────

        [Fact]
        public void FailedCandidateSuppressionIsRegionBoundedAndExpires()
        {
            var tracker = new LootSystem.CandidateSuppressionTracker();
            var bounds = new Rectangle(100, 100, 20, 20);
            DateTime now = DateTime.UtcNow;

            Assert.False(tracker.IsSuppressed(bounds, now));
            (int firstCount, int firstMs) = tracker.RecordFailure(bounds, now);
            Assert.Equal(1, firstCount);
            Assert.True(tracker.IsSuppressed(bounds, now.AddMilliseconds(10)));

            // Same artifact, same region: bounded backoff grows.
            (int secondCount, int secondMs) = tracker.RecordFailure(bounds, now.AddMilliseconds(10));
            Assert.Equal(2, secondCount);
            Assert.True(secondMs > firstMs);

            // Meaningful visual change (region moved) is new evidence.
            var moved = new Rectangle(180, 100, 20, 20);
            Assert.False(tracker.IsSuppressed(moved, now.AddMilliseconds(20)));

            // Expiry is bounded too: suppression never blacklists the location.
            tracker.RecordFailure(bounds, now);
            Assert.False(tracker.IsSuppressed(
                bounds, now.AddMilliseconds(BotConstants.Loot.PassivePreScanSuppressionMaxMs + 1)));
        }

        [Fact]
        public void UnavailableTravelVerificationReleasesNavigationWithoutClaimingEmpty()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);

            long requestId = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            loot.NotifyNavigationHeld(requestId);
            InvokePrivate(movement, "RefreshLootBarrier");
            RunMandatoryService(loot);
            Assert.True(movement.IsLootNavigationBlocked);

            InvokePrivate(loot, "MarkTravelVerificationUnavailable", requestId, "capture failed");
            Assert.True(loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Unavailable, status.State);
            Assert.False(status.CameraVerified);

            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.Contains(lines, line => line.Contains("not treated as an empty result", StringComparison.Ordinal));
        }

        // ── Test 5 — confirmed item ─────────────────────────────────────────────

        [Fact]
        public void ConfirmedItemCompletesTheVerificationAndOnlyThenReleasesNavigation()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);

            long requestId = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            loot.NotifyNavigationHeld(requestId);
            InvokePrivate(movement, "RefreshLootBarrier");
            RunMandatoryService(loot);

            InvokePrivate(loot, "RecordMandatoryItemCollected", requestId);
            Assert.True(movement.IsLootNavigationBlocked);

            CompleteMandatory(loot, requestId, ValidEmptyOutcome());
            Assert.True(loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Completed, status.State);
            Assert.True(status.CameraVerified);
            Assert.Equal(1, status.ItemsCollected);
            Assert.True(IsLootStateIdle(loot));

            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.False(loot.IsLootCycleActive);
        }

        // ── Test 6 — last mob killed ────────────────────────────────────────────

        [Fact]
        public void PostKillSignalArmsMandatoryScanAndBlocksNavigationUntilVerified()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);
            SetMovingForward(movement, true);

            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            Assert.True(movement.IsLootNavigationBlocked);
            Assert.True(BarrierHoldsInput(movement));
            Assert.Equal(LootBarrierReason.PostKill, movement.LootBarrier.Reason);
            long requestId = movement.LootBarrier.RequestId;
            Assert.True(requestId > 0);

            InvokePrivate(movement, "StartMoving");
            Assert.False(IsMovingForward(movement), "No waypoint movement may resume before the post-kill scan completes.");

            loot.NotifyNavigationHeld(requestId);
            RunMandatoryService(loot);
            Assert.True(loot.IsLootCycleActive);

            CompleteMandatory(loot, requestId, ValidEmptyOutcome());
            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.Contains(lines, line => line.Contains("PostKill", StringComparison.Ordinal));
        }

        [Fact]
        public void PostKillUnavailableIsNeverAValidEmptyScan()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(77, LootBarrierReason.PostKill);

            barrier.ApplyStatus(Snapshot(
                77, LootBarrierReason.PostKill, LootRequestState.Unavailable,
                cameraVerified: false, detail: "capture failed"));

            Assert.True(barrier.IsBlockingNavigation);
            Assert.True(barrier.HasTerminalFailure);
            Assert.NotEqual(LootBarrierPhase.Unavailable, barrier.Phase);
            Assert.Contains("unavailable", barrier.Detail, StringComparison.Ordinal);
        }

        // ── Test 7 — waypoint reached ───────────────────────────────────────────

        [Fact]
        public async Task ReachingLootWaypointDoesNotArmAnyMandatoryScan()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);
            SetMovingForward(movement, true);

            bool advanced = await InvokeAdvanceAsync(movement, 1000, 1000);

            Assert.True(advanced);
            Assert.Equal(0, QueueCount(movement));
            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.False(loot.HasActiveMandatoryLootRequest);
            Assert.DoesNotContain(lines, line => line.Contains("LootBarrier] Armed", StringComparison.Ordinal));
        }

        // ── Test 8 — route resync ───────────────────────────────────────────────

        [Fact]
        public void RouteResyncWaitsForPostKillButIsNotAscanTrigger()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(88, LootBarrierReason.PostKill);

            Assert.False(MovementSystem.CanRunRouteResyncAfterCombat(
                pending: true, lootBarrierBlocked: barrier.IsBlockingNavigation));

            barrier.ApplyStatus(Snapshot(
                88, LootBarrierReason.PostKill, LootRequestState.Completed,
                cameraVerified: true));

            Assert.True(MovementSystem.CanRunRouteResyncAfterCombat(
                pending: true, lootBarrierBlocked: barrier.IsBlockingNavigation));

            // Resync bookkeeping by itself never creates a request; only the real kill
            // signal (TabAfterKill) or a pre-scan candidate can.
            using var loot = CreateLoot(_ => { });
            Assert.False(loot.HasActiveMandatoryLootRequest);
        }

        // ── Test 9 — camera separation ──────────────────────────────────────────

        [Fact]
        public void LootCaptureRequiresExactDistanceLockSettlementAndOwnership()
        {
            Assert.Equal((short)17050, BotConstants.Camera.LootScanDistance);
            Assert.Equal((short)17050, BotConstants.Camera.SodSopLootScanDistance);
            Assert.Equal(BotConstants.Camera.LootScanDistance, LootSystem.NormalLootScanDistance);

            short distance = LootSystem.NormalLootScanDistance;
            short lockValue = BotConstants.Camera.StationaryVerticalLock;
            Assert.Equal((short)16310, lockValue);
            Assert.Equal((short)16160, BotConstants.Camera.MovementVerticalLock);

            Assert.True(LootSystem.IsLootCaptureAuthorized(
                distance, distance, lockValue, lockValue,
                stabilizationElapsed: true, ownershipHeld: true));

            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, (short)17000, lockValue, lockValue,
                stabilizationElapsed: true, ownershipHeld: true));
            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, (short)(distance - 1), lockValue, lockValue,
                stabilizationElapsed: true, ownershipHeld: true));
            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, distance, lockValue, (short)(lockValue - 1),
                stabilizationElapsed: true, ownershipHeld: true));
            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, distance, lockValue, lockValue,
                stabilizationElapsed: false, ownershipHeld: true));
            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, distance, lockValue, lockValue,
                stabilizationElapsed: true, ownershipHeld: false));
        }

        [Fact]
        public void VerifiedLootScanUsesStationaryLockAndBlocksWCameraWritesUntilRelease()
        {
            const uint pid = 909;
            const ulong moduleBase = 0x710000;
            const ulong cameraBase = 0x3700000;
            var memoryBytes = new Dictionary<ulong, byte[]>
            {
                [moduleBase + BotConstants.MemoryOffsets.CameraPtr] = BitConverter.GetBytes(cameraBase),
                [cameraBase + BotConstants.MemoryOffsets.CameraDistance] = BitConverter.GetBytes((short)17050),
                [cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset] =
                    BitConverter.GetBytes(BotConstants.Camera.StationaryVerticalLock)
            };

            GameMemoryService.ReadMemoryDelegate read =
                (uint readPid, ulong address, byte[] buffer, out uint bytesRead) =>
                {
                    bytesRead = 0;
                    if (!memoryBytes.TryGetValue(address, out byte[]? data) || data.Length < buffer.Length)
                        return false;
                    Buffer.BlockCopy(data, 0, buffer, 0, buffer.Length);
                    bytesRead = (uint)buffer.Length;
                    return true;
                };
            GameMemoryService.WriteMemoryDelegate write =
                (uint writePid, ulong address, byte[] buffer, out uint bytesWritten) =>
                {
                    memoryBytes[address] = (byte[])buffer.Clone();
                    bytesWritten = (uint)buffer.Length;
                    return true;
                };

            var memory = new GameMemoryService(pid, read, write, moduleBase, 8, _ => { });
            using var controller = new WKeyCameraController(memory, _ => { });
            using var loot = new LootSystem(memory, _ => { }, controller);
            MethodInfo ensure = typeof(LootSystem).GetMethod("EnsureLootScanCamera", PrivateInstance)
                ?? throw new InvalidOperationException("EnsureLootScanCamera was not found.");
            object?[] arguments = { null };

            Assert.True((bool)(ensure.Invoke(loot, arguments) ?? false));
            Assert.Equal((short)17050,
                BitConverter.ToInt16(memoryBytes[cameraBase + BotConstants.MemoryOffsets.CameraDistance], 0));
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock,
                BitConverter.ToInt16(memoryBytes[cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset], 0));

            WKeyCameraOwner movementOwner = controller.CreateOwner("loot scan race regression", _ => { });
            Assert.False(controller.TryAcquireW(movementOwner));

            MethodInfo release = typeof(LootSystem).GetMethod("ReleaseLootScanCameraLocked", PrivateInstance)
                ?? throw new InvalidOperationException("ReleaseLootScanCameraLocked was not found.");
            release.Invoke(loot, new object[] { "test scan complete" });

            Assert.True(controller.TryAcquireW(movementOwner));
            controller.ReleaseW(movementOwner);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock,
                BitConverter.ToInt16(memoryBytes[cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset], 0));
        }

        [Fact]
        public void MovementCameraWriteCannotOverrideLootScanCameraOwnership()
        {
            using var loot = CreateLoot(_ => { });
            var ownedField = typeof(LootSystem).GetField("_lootScanCameraOwned", PrivateInstance);
            Assert.NotNull(ownedField);

            int writes = 0;
            ownedField!.SetValue(loot, true);
            Assert.False(loot.TryRunMovementCameraAction(() => writes++));
            Assert.Equal(0, writes);

            ownedField.SetValue(loot, false);
            Assert.True(loot.TryRunMovementCameraAction(() => writes++));
            Assert.Equal(1, writes);
        }

        [Fact]
        public void StandaloneLootCreatesNoCameraSessionAndKeepsTheProvidedSharedController()
        {
            const uint standalonePid = 911;
            const ulong standaloneBase = 0x730000;
            var standaloneMemory = CreateIdentityMemory(standalonePid, standaloneBase, _ => { });

            Assert.False(WKeyCameraController.HasActiveSession(standalonePid, standaloneBase));
            using (var standaloneLoot = new LootSystem(standaloneMemory, _ => { }))
            {
                Assert.False(WKeyCameraController.HasActiveSession(standalonePid, standaloneBase));
                Assert.Null(typeof(LootSystem)
                    .GetField("_wKeyCameraController", PrivateInstance)!
                    .GetValue(standaloneLoot));
            }

            const uint sharedPid = 912;
            const ulong sharedBase = 0x740000;
            var sharedMemory = CreateIdentityMemory(sharedPid, sharedBase, _ => { });
            using var controller = new WKeyCameraController(sharedMemory, _ => { });
            using var sharedLoot = new LootSystem(sharedMemory, _ => { }, controller);

            Assert.True(WKeyCameraController.HasActiveSession(sharedPid, sharedBase));
            Assert.Same(controller, typeof(LootSystem)
                .GetField("_wKeyCameraController", PrivateInstance)!
                .GetValue(sharedLoot));
        }

        // ── Test 10 — capture failure ───────────────────────────────────────────

        [Fact]
        public void InvalidPreScanGeometryIsCaptureUnavailableNotACandidate()
        {
            using var bitmap = new Bitmap(80, 80);
            LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                bitmap, new Rectangle(-10, -10, 5, 5),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.CaptureUnavailable, result.Outcome);
            Assert.Equal(Rectangle.Empty, result.CandidateBounds);
        }

        [Fact]
        public void CaptureFailureIsNeverAValidEmptyScanOrSuccessfulBarrier()
        {
            LootScanPassResult combined = LootSystem.CombineNormalLootScanResults(
                LootScanPassResult.NoItem, LootScanPassResult.CaptureFailed, cameraVerified: true);
            Assert.Equal(LootScanPassResult.CaptureFailed, combined);
            Assert.NotEqual(LootScanPassResult.NoItem, combined);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(5, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(5, LootBarrierReason.PostKill, LootRequestState.Failed, cameraVerified: false));
            Assert.True(barrier.IsBlockingNavigation);
        }

        // ── Test 11 — other movement modes ──────────────────────────────────────

        [Fact]
        public void SeparatePinkScanRequestsAreIgnoredOutsidePinkOnlyMode()
        {
            using var loot = CreateLoot(_ => { });

            loot.RequestPinkScan();
            Assert.False(loot.IsPinkScanPendingOrActive);

            loot.PinkLootOnlyMode = true;
            loot.RequestPinkScan();
            Assert.True(loot.IsPinkScanPendingOrActive);
            Assert.False(loot.IsLootCycleActive);

            loot.PinkLootOnlyMode = false;
            Assert.False(loot.IsPinkScanPendingOrActive);
        }

        [Fact]
        public void OnlyMoveAndMoveAndAttackDoNotUseTheMandatoryBarrier()
        {
            Assert.False(MovementSystem.RequiresMandatoryLootBarrier(BotMode.OnlyMove));
            Assert.False(MovementSystem.RequiresMandatoryLootBarrier(BotMode.MoveAndAttack));
            Assert.True(MovementSystem.RequiresMandatoryLootBarrier(BotMode.MoveAndAttackAndLoot));

            using var loot = CreateLoot(_ => { });
            loot.PinkLootOnlyMode = true;
            Assert.True(loot.PinkLootOnlyMode);
            Assert.False(loot.MoveAndAttackAndLootMode);
        }

        // ── Test 12 — no duplicate operations ───────────────────────────────────

        [Fact]
        public void RepeatedRequestsAndServicesDoNotCreateDuplicateObligations()
        {
            using var loot = CreateLoot(_ => { });
            long first = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            long second = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            Assert.Equal(first, second);

            loot.NotifyNavigationHeld(first);
            RunMandatoryService(loot);
            RunMandatoryService(loot);

            Assert.True(loot.TryGetMandatoryLootRequestStatus(first, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Running, status.State);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(first, LootBarrierReason.TravelCandidateVerification);
            barrier.Arm(first, LootBarrierReason.TravelCandidateVerification);
            Assert.Equal(first, barrier.RequestId);
            Assert.True(barrier.IsActive);
        }

        // ── Scan-pass result preservation ───────────────────────────────────────

        [Theory]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.NoItem, (int)LootScanPassResult.NoItem)]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.CaptureFailed, (int)LootScanPassResult.CaptureFailed)]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.CameraVerificationFailed, (int)LootScanPassResult.CameraVerificationFailed)]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.InvalidGeometry, (int)LootScanPassResult.InvalidGeometry)]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.Incomplete, (int)LootScanPassResult.Incomplete)]
        [InlineData((int)LootScanPassResult.CaptureFailed, null, (int)LootScanPassResult.CaptureFailed)]
        [InlineData((int)LootScanPassResult.Cancelled, null, (int)LootScanPassResult.Cancelled)]
        public void NormalLootPassRequiresBothValidNoItemRegions(
            int small,
            int? big,
            int expected)
        {
            Assert.Equal(
                (LootScanPassResult)expected,
                LootSystem.CombineNormalLootScanResults(
                    (LootScanPassResult)small,
                    big.HasValue ? (LootScanPassResult)big.Value : null,
                    cameraVerified: true));
        }

        [Fact]
        public void EmptySmallScanAloneIsNotACompletePass()
        {
            Assert.Equal(
                LootScanPassResult.Incomplete,
                LootSystem.CombineNormalLootScanResults(
                    LootScanPassResult.NoItem, null, cameraVerified: true));
        }

        [Fact]
        public void CompletionWithoutCameraVerificationFailsTheBarrier()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(99, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(99, LootBarrierReason.PostKill, LootRequestState.Completed, cameraVerified: false));

            Assert.True(barrier.IsBlockingNavigation);
            Assert.True(barrier.HasTerminalFailure);
            Assert.Contains("without verified loot camera", barrier.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void StaleCompletionForAnotherGenerationCannotReleaseTheArmedRequest()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(100, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(101, LootBarrierReason.PostKill, LootRequestState.Completed, cameraVerified: true));

            Assert.True(barrier.IsBlockingNavigation);
        }

        // ── Candidate-shape / text-glyph filtering ──────────────────────────────

        [Fact]
        public void TextGlyphRowsAreNotPlausibleLootCandidates()
        {
            using var bitmap = new Bitmap(200, 100);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(AquamarineLoot))
            {
                graphics.Clear(Color.Black);
                for (int i = 0; i < 7; i++)
                    graphics.FillRectangle(brush, 20 + i * 6, 30, 4, 4);
            }

            LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                bitmap, new Rectangle(0, 0, 200, 100),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.NoCandidate, result.Outcome);
            Assert.Equal(0, result.CandidateComponents);
        }

        [Fact]
        public void CharacterCenterAreaIsScannedByThePreScan()
        {
            using var bitmap = new Bitmap(200, 200);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(AquamarineLoot))
            {
                graphics.Clear(Color.Black);
                graphics.FillRectangle(brush, 90, 90, 14, 14);
            }

            LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                bitmap,
                new Rectangle(0, 0, 200, 200),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.PotentialCandidate, result.Outcome);
            Assert.True(result.CandidateComponents >= 1);
        }

        [Fact]
        public void TooManyCandidateComponentsAreTreatedAsUiClutter()
        {
            using var bitmap = new Bitmap(300, 300);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(AquamarineLoot))
            {
                graphics.Clear(Color.Black);
                for (int i = 0; i < 6; i++)
                {
                    for (int j = 0; j < 6; j++)
                    {
                        if (i == j) continue;
                        graphics.FillRectangle(brush, 20 + i * 40, 20 + j * 40, 12, 12);
                    }
                }
            }

            LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                bitmap, new Rectangle(0, 0, 300, 300),
                maxCandidateComponents: 8);

            Assert.Equal(LootPreScanOutcome.NoCandidate, result.Outcome);
            Assert.True(result.CandidateComponents > 8);
        }

        // ── Post-kill close-first retarget regressions ──────────────────────────

        [Fact]
        public async Task TabAfterKillArmsMandatoryLootWithoutTabOrWaypointMovement()
        {
            bool groupingWasEnabled = MobGroupingRuntimeSettings.Enabled;
            try
            {
                MobGroupingRuntimeSettings.Enabled = false;
                using var scenario = CreateRetargetScenario();
                var handler = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)!.GetValue(scenario.Movement)!;
                typeof(CombatHandler).GetField("_wasAttacking", PrivateInstance)!.SetValue(handler, true);

                await scenario.Movement.Update(CancellationToken.None);

                Assert.True(scenario.Loot.TryGetActiveMandatoryLootRequest(out long requestId, out LootBarrierReason reason));
                Assert.Equal(LootBarrierReason.PostKill, reason);
                Assert.True(requestId > 0);
                Assert.Equal(requestId, scenario.Movement.LootBarrier.RequestId);
                Assert.Equal(requestId, (long)typeof(MovementSystem)
                    .GetField("_postKillRetargetRequestId", PrivateInstance)!.GetValue(scenario.Movement)!);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Equal(0, scenario.Movement.ForwardKeyDowns);

                await scenario.Movement.Update(CancellationToken.None);
                Assert.True(scenario.Movement.IsLootNavigationBlocked);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            }
            finally
            {
                MobGroupingRuntimeSettings.Enabled = groupingWasEnabled;
            }
        }

        [Fact]
        public async Task FullPostKillFlowIsTargetReleaseLoot16880TabThenAuthorizedAttack()
        {
            bool groupingWasEnabled = MobGroupingRuntimeSettings.Enabled;
            try
            {
                MobGroupingRuntimeSettings.Enabled = false;
                using var scenario = CreateRetargetScenario();
                scenario.Memory.TargetId = 16385;
                object handler = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)!
                    .GetValue(scenario.Movement)!;
                SetPrivateField(handler, "_wasAttacking", true);
                SetPrivateField(handler, "_combatIdleStartTime", DateTime.Now.AddMilliseconds(-1200));

                await scenario.Movement.Update(CancellationToken.None);

                Assert.True(scenario.Loot.TryGetActiveMandatoryLootRequest(out long requestId, out LootBarrierReason reason));
                Assert.Equal(LootBarrierReason.PostKill, reason);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Equal(0, scenario.Movement.SkillThreeDowns);
                Assert.Contains(scenario.Log, line => line.Contains("[PostKillTarget] Request=", StringComparison.Ordinal));

                await scenario.Movement.Update(CancellationToken.None);
                Assert.Equal(0, scenario.Memory.TargetId);
                Assert.Equal(new[] { "Mouse:LeftDown", "Mouse:LeftUp" }, scenario.Movement.InputOrder);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Equal(0, scenario.Movement.SkillThreeDowns);

                RunMandatoryService(scenario.Loot);
                Assert.True(scenario.Loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus running));
                Assert.Equal(LootRequestState.Running, running.State);
                CompleteMandatory(scenario.Loot, requestId, ValidEmptyOutcome());

                await scenario.Movement.Update(CancellationToken.None);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Contains(scenario.Log, line => line.Contains(
                    $"PostKill completed request={requestId} cameraVerified=True", StringComparison.Ordinal));

                scenario.Movement.OnTargetCycle = () => scenario.Memory.TargetId = 16386;
                await scenario.Movement.Update(CancellationToken.None);
                Assert.Equal(new[] { (short)16880 }, scenario.Movement.TargetCycleCameraDistances);
                Assert.Equal((short)16880, scenario.Memory.CameraDistance);

                await scenario.Movement.Update(CancellationToken.None);
                Assert.Equal(1, scenario.Movement.TargetCyclePresses);
                Assert.Equal(1, scenario.Movement.SkillThreeDowns);
                Assert.Equal(new[] { "Mouse:LeftDown", "Mouse:LeftUp", "TAB", "Skill3Down" },
                    scenario.Movement.InputOrder);
                Assert.True(scenario.Presence.ObserveCount > 0);

                int clickUp = scenario.Movement.InputOrder.IndexOf("Mouse:LeftUp");
                int tab = scenario.Movement.InputOrder.IndexOf("TAB");
                int attack = scenario.Movement.InputOrder.IndexOf("Skill3Down");
                Assert.True(clickUp < tab && tab < attack);
                Assert.True(scenario.Log.FindIndex(line => line.Contains("PostKill completed request=", StringComparison.Ordinal)) <
                            scenario.Log.FindIndex(line => line.Contains("TAB sent stage=VeryLow", StringComparison.Ordinal)));
            }
            finally
            {
                MobGroupingRuntimeSettings.Enabled = groupingWasEnabled;
            }
        }

        [Fact]
        public async Task FinalStandbyPostKillUsesCurrentModeAndKeepsMandatoryBarrier()
        {
            bool groupingWasEnabled = MobGroupingRuntimeSettings.Enabled;
            try
            {
                MobGroupingRuntimeSettings.Enabled = false;
                using var scenario = CreateRetargetScenario();
                SetPrivateField(scenario.Movement, "_finalStandbyActive", true);
                SetPrivateField(scenario.Movement, "_finalStandbyMode", BotMode.MoveAndAttackAndLoot);
                SetPrivateField(scenario.Movement, "_finalStandbyX", 10f);
                SetPrivateField(scenario.Movement, "_finalStandbyY", 0f);
                SetPrivateField(scenario.Movement, "_finalStandbyAtkDis", (short)33);
                SetPrivateField(scenario.Movement, "_finalStandbyWaypoint",
                    new Waypoint(10, 0, MovementPrecision.Medium, BotMode.MoveAndAttackAndLoot));
                object handler = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)!
                    .GetValue(scenario.Movement)!;
                SetPrivateField(handler, "_wasAttacking", true);

                await scenario.Movement.Update(CancellationToken.None);

                Assert.True(scenario.Loot.TryGetActiveMandatoryLootRequest(out long requestId, out LootBarrierReason reason));
                Assert.Equal(LootBarrierReason.PostKill, reason);
                Assert.Equal(requestId, (long)typeof(MovementSystem)
                    .GetField("_postKillRetargetRequestId", PrivateInstance)!.GetValue(scenario.Movement)!);
                Assert.True(scenario.Movement.IsLootNavigationBlocked);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Equal(0, scenario.Movement.ForwardKeyDowns);

                // While the obligation runs, standby must not walk back to the final
                // waypoint and must not cycle targets at the loot camera.
                await scenario.Movement.Update(CancellationToken.None);
                Assert.True(scenario.Movement.IsLootNavigationBlocked);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Equal(0, scenario.Movement.ForwardKeyDowns);

                RunMandatoryService(scenario.Loot);
                CompleteMandatory(scenario.Loot, requestId, ValidEmptyOutcome());

                await scenario.Movement.Update(CancellationToken.None);
                Assert.False(scenario.Movement.IsLootNavigationBlocked);
                Assert.Equal("VeryLowSearch", typeof(MovementSystem)
                    .GetField("_combatRetargetCameraStage", PrivateInstance)!.GetValue(scenario.Movement)!.ToString());
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);

                await scenario.Movement.Update(CancellationToken.None);
                Assert.Equal(new[] { (short)16880 }, scenario.Movement.TargetCycleCameraDistances);
                Assert.Equal(1, scenario.Movement.TargetCyclePresses);
                Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            }
            finally
            {
                MobGroupingRuntimeSettings.Enabled = groupingWasEnabled;
            }
        }

        [Fact]
        public async Task DisableAllLootStillStartsPostKillSearchAt16880WithoutCreatingRequest()
        {
            bool groupingWasEnabled = MobGroupingRuntimeSettings.Enabled;
            try
            {
                MobGroupingRuntimeSettings.Enabled = false;
                using var scenario = CreateRetargetScenario();
                scenario.Movement.DisableAllLootOverride = true;
                object handler = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)!
                    .GetValue(scenario.Movement)!;
                SetPrivateField(handler, "_wasAttacking", true);

                await scenario.Movement.Update(CancellationToken.None);
                Assert.False(scenario.Loot.HasActiveMandatoryLootRequest);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);

                await scenario.Movement.Update(CancellationToken.None);
                Assert.False(scenario.Loot.HasActiveMandatoryLootRequest);
                Assert.Equal(new[] { (short)16880 }, scenario.Movement.TargetCycleCameraDistances);
                Assert.Contains(scenario.Log, line => line.Contains("automatic loot disabled", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                MobGroupingRuntimeSettings.Enabled = groupingWasEnabled;
            }
        }

        [Fact]
        public async Task PinkLootOnlyModeStillChecksCloseMobBeforeRequestingPinkScan()
        {
            bool groupingWasEnabled = MobGroupingRuntimeSettings.Enabled;
            try
            {
                MobGroupingRuntimeSettings.Enabled = false;
                using var scenario = CreateRetargetScenario(BotMode.MoveAndAttack);
                scenario.Loot.PinkLootOnlyMode = true;
                object handler = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)!
                    .GetValue(scenario.Movement)!;
                SetPrivateField(handler, "_wasAttacking", true);

                await scenario.Movement.Update(CancellationToken.None);
                Assert.False(scenario.Loot.HasActiveMandatoryLootRequest);
                Assert.False(scenario.Loot.IsPinkScanPendingOrActive);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);

                await scenario.Movement.Update(CancellationToken.None);
                Assert.Equal(new[] { (short)16880 }, scenario.Movement.TargetCycleCameraDistances);
                Assert.False(scenario.Loot.IsPinkScanPendingOrActive);

                await scenario.Movement.Update(CancellationToken.None);
                Assert.True(scenario.Loot.IsPinkScanPendingOrActive);
                Assert.False(scenario.Loot.HasActiveMandatoryLootRequest);
                Assert.Equal(1, scenario.Movement.TargetCyclePresses);
            }
            finally
            {
                MobGroupingRuntimeSettings.Enabled = groupingWasEnabled;
            }
        }

        [Fact]
        public void TargetActionYieldsToLootCameraAndPendingOrRunningMandatoryRequests()
        {
            using var loot = CreateLoot(_ => { });
            FieldInfo owned = typeof(LootSystem).GetField("_lootScanCameraOwned", PrivateInstance)!;
            bool callbackRan = false;

            owned.SetValue(loot, true);
            Assert.False(loot.TryRunMovementTargetAction(() => callbackRan = true));
            Assert.False(callbackRan);

            owned.SetValue(loot, false);
            long requestId = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            Assert.False(loot.TryRunMovementTargetAction(() => callbackRan = true));
            loot.NotifyNavigationHeld(requestId);
            RunMandatoryService(loot);
            Assert.True(loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus running));
            Assert.Equal(LootRequestState.Running, running.State);
            Assert.False(loot.TryRunMovementTargetAction(() => callbackRan = true));
            Assert.False(callbackRan);

            // A verified completion returns ownership: targeting may run again without any
            // mandatory request owning the camera/cursor.
            CompleteMandatory(loot, requestId, ValidEmptyOutcome());
            Assert.True(loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus completed));
            Assert.Equal(LootRequestState.Completed, completed.State);
            Assert.True(completed.CameraVerified);
            Assert.True(loot.TryRunMovementTargetAction(() => callbackRan = true));
            Assert.True(callbackRan);

            using var deferredLoot = CreateLoot(_ => { });
            long deferredId = deferredLoot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            deferredLoot.NotifyNavigationHeld(deferredId);
            RunMandatoryService(deferredLoot, mobSelected: true);
            Assert.True(deferredLoot.TryGetMandatoryLootRequestStatus(deferredId, out LootRequestStatus deferred));
            Assert.Equal(LootRequestState.Deferred, deferred.State);
            Assert.True(deferredLoot.TryRunMovementTargetAction(() => callbackRan = true));
            Assert.True(callbackRan);
        }

        [Fact]
        public async Task VerifiedPostKillCompletionStartsAt16880AndUsesOneCameraVerifiedTab()
        {
            using var scenario = CreateRetargetScenario();
            InvokePrivate(scenario.Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            long requestId = scenario.Movement.LootBarrier.RequestId;
            SetPrivateField(scenario.Movement, "_postKillRetargetRequestId", requestId);

            RunMandatoryService(scenario.Loot);
            CompleteMandatory(scenario.Loot, requestId, ValidEmptyOutcome());
            InvokePrivate(scenario.Movement, "RefreshLootBarrier");

            Assert.Equal(LootBarrierPhase.Completed, scenario.Movement.LootBarrier.Phase);
            Assert.True(scenario.Movement.LootBarrier.CameraVerified);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);

            Assert.True(await InvokePrivateBoolAsync(
                scenario.Movement, "HandlePostKillRetargetRequestAsync", CancellationToken.None));
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Equal("VeryLowSearch", typeof(MovementSystem)
                .GetField("_combatRetargetCameraStage", PrivateInstance)!.GetValue(scenario.Movement)!.ToString());

            scenario.Movement.OnTargetCycle = () => scenario.Memory.TargetId = 16386;
            Assert.True(await InvokePrivateBoolAsync(
                scenario.Movement, "HandleCombatRetargetCameraStageAsync",
                BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));

            Assert.Equal(new[] { (short)16880 }, scenario.Movement.TargetCycleCameraDistances);
            Assert.Equal((short)16880, scenario.Memory.CameraDistance);
            Assert.Contains(scenario.Log, line => line.Contains("TAB sent stage=VeryLow", StringComparison.Ordinal));
            Assert.Contains(scenario.Log, line => line.Contains("PostKill completed request=", StringComparison.Ordinal));

            await scenario.Movement.Update(CancellationToken.None);
            Assert.Equal(1, scenario.Movement.TargetCyclePresses);
            Assert.Equal(1, scenario.Movement.SkillThreeDowns);
            Assert.Contains(scenario.Log, line => line.Contains("Monster selected targetId=16386", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ForeignCompletedRequestCannotReleaseOrStartPostKillRetarget()
        {
            using var scenario = CreateRetargetScenario();
            long requestId = scenario.Loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            scenario.Loot.NotifyNavigationHeld(requestId);
            InvokePrivate(scenario.Movement, "RefreshLootBarrier");
            SetPrivateField(scenario.Movement, "_postKillRetargetRequestId", requestId);

            scenario.Movement.LootBarrier.ApplyStatus(Snapshot(
                requestId + 1, LootBarrierReason.PostKill, LootRequestState.Completed,
                cameraVerified: true));

            Assert.True(await InvokePrivateBoolAsync(
                scenario.Movement, "HandlePostKillRetargetRequestAsync", CancellationToken.None));
            Assert.True(scenario.Movement.IsLootNavigationBlocked);
            Assert.Equal("None", typeof(MovementSystem)
                .GetField("_combatRetargetCameraStage", PrivateInstance)!.GetValue(scenario.Movement)!.ToString());
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
        }

        [Fact]
        public async Task RetargetLadderUses16880Then16910Then16950AndRestoresWaypointCamera()
        {
            using var scenario = CreateRetargetScenario();
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");

            Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));
            Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));
            Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));
            Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));

            Assert.Equal(new[] { (short)16880, (short)16910, (short)16950 },
                scenario.Movement.TargetCycleCameraDistances);
            Assert.Equal((short)17020, scenario.Memory.CameraDistance);
            Assert.Equal("None", typeof(MovementSystem)
                .GetField("_combatRetargetCameraStage", PrivateInstance)!.GetValue(scenario.Movement)!.ToString());
        }

        [Fact]
        public async Task SelectedMonsterStopsRetargetLadderAndIsRevalidated()
        {
            using var scenario = CreateRetargetScenario();
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");
            scenario.Movement.OnTargetCycle = () => scenario.Memory.TargetId = 16386;

            await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None);
            int observationsBeforeCheck = scenario.Presence.ObserveCount;
            bool stageConsumed = await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None);

            Assert.False(stageConsumed);
            Assert.True(scenario.Presence.ObserveCount > observationsBeforeCheck);
            Assert.Equal(1, scenario.Movement.TargetCyclePresses);
            Assert.Equal("None", typeof(MovementSystem)
                .GetField("_combatRetargetCameraStage", PrivateInstance)!.GetValue(scenario.Movement)!.ToString());
            Assert.Contains(scenario.Log, line => line.Contains("Monster selected targetId=16386", StringComparison.Ordinal));
        }

        [Fact]
        public async Task LootTakingCameraBetweenApplyAndTabDefersWithoutDeadlockingStage()
        {
            using var scenario = CreateRetargetScenario();
            FieldInfo owned = typeof(LootSystem).GetField("_lootScanCameraOwned", PrivateInstance)!;
            scenario.Memory.AfterCameraWrite = () => owned.SetValue(scenario.Loot, true);
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");

            Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));
            Assert.Equal((short)16880, scenario.Memory.CameraDistance);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.False((bool)typeof(MovementSystem)
                .GetField("_combatRetargetAwaitingSelection", PrivateInstance)!.GetValue(scenario.Movement)!);

            owned.SetValue(scenario.Loot, false);
            scenario.Memory.AfterCameraWrite = null;
            Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));
            Assert.Equal(1, scenario.Movement.TargetCyclePresses);
            Assert.Equal((short)16880, scenario.Movement.TargetCycleCameraDistances[0]);
        }

        [Fact]
        public async Task WrongZoomRetriesCameraWriteThreeTimesWithoutSendingTab()
        {
            using var scenario = CreateRetargetScenario();
            scenario.Memory.CameraDistance = 17050;
            scenario.Memory.IgnoreCameraWrites = true;
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");

            for (int attempt = 0; attempt < 3; attempt++)
                Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                    "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));

            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Equal((short)17050, scenario.Memory.CameraDistance);
            Assert.False((bool)typeof(MovementSystem)
                .GetField("_combatRetargetAwaitingSelection", PrivateInstance)!.GetValue(scenario.Movement)!);
            Assert.True((bool)typeof(MovementSystem)
                .GetField("_combatRetargetFailed", PrivateInstance)!.GetValue(scenario.Movement)!);
            Assert.Contains(scenario.Log, line => line.Contains("no blind TAB sent", StringComparison.Ordinal));
        }

        [Fact]
        public async Task FinalStandbyRetargetRestoreUsesLastWaypointCamera()
        {
            using var scenario = CreateRetargetScenario();
            var standbyWaypoint = new Waypoint(10, 0, MovementPrecision.Medium,
                BotMode.MoveAndAttackAndLoot, cameraDistanceLock: 17120);
            SetPrivateField(scenario.Movement, "_finalStandbyActive", true);
            SetPrivateField(scenario.Movement, "_finalStandbyMode", BotMode.MoveAndAttackAndLoot);
            SetPrivateField(scenario.Movement, "_finalStandbyWaypoint", standbyWaypoint);
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");
            short restoreDistance = (short)typeof(MovementSystem)
                .GetMethod("GetRetargetRestoreCameraDistance", PrivateInstance)!
                .Invoke(scenario.Movement, null)!;
            Assert.Equal((short)17120, restoreDistance);

            for (int i = 0; i < 4; i++)
                await InvokePrivateBoolAsync(scenario.Movement,
                    "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttack, restoreDistance, CancellationToken.None);

            Assert.Equal(new[] { (short)16880, (short)16910, (short)16950 },
                scenario.Movement.TargetCycleCameraDistances);
            Assert.Equal((short)17120, scenario.Memory.CameraDistance);
        }

        [Fact]
        public void PostKillTargetReleaseUsesVerifiedClientCenterAndDoesNotReserveCombatGateReturn()
        {
            using var scenario = CreateRetargetScenario();
            scenario.Memory.TargetId = 16386;
            InvokePrivate(scenario.Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            SetPrivateField(scenario.Movement, "_postKillTargetReleaseId", 16386);

            InvokePrivate(scenario.Movement, "ProcessPostKillTargetRelease", CancellationToken.None);

            Assert.Equal(new[] { (448 + 512, 131 + 384) }, scenario.Movement.CursorMoves);
            Assert.Equal(new[] { MouseOperations.MouseEventFlags.LeftDown, MouseOperations.MouseEventFlags.LeftUp },
                scenario.Movement.MouseEvents);
            Assert.Equal(0, scenario.Memory.TargetId);
            Assert.False((bool)typeof(MovementSystem)
                .GetField("_combatGateReturnHandoffReserved", PrivateInstance)!.GetValue(scenario.Movement)!);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.True(scenario.Movement.IsLootNavigationBlocked);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Contains(scenario.Log, line => line.Contains("LeftClick center — release verified", StringComparison.Ordinal));
        }

        [Fact]
        public void PostKillTargetReleaseDefersForFocusOrLootCursorAndPreservesObligationAfterTwoClicks()
        {
            using var scenario = CreateRetargetScenario();
            scenario.Memory.TargetId = 16386;
            InvokePrivate(scenario.Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            long requestId = scenario.Movement.LootBarrier.RequestId;
            SetPrivateField(scenario.Movement, "_postKillTargetReleaseId", 16386);

            scenario.Movement.Foreground = false;
            InvokePrivate(scenario.Movement, "ProcessPostKillTargetRelease", CancellationToken.None);
            Assert.Empty(scenario.Movement.MouseEvents);
            Assert.Equal(0, (int)typeof(MovementSystem)
                .GetField("_postKillTargetReleaseAttempts", PrivateInstance)!.GetValue(scenario.Movement)!);

            scenario.Movement.Foreground = true;
            typeof(LootSystem).GetField("_lootScanCameraOwned", PrivateInstance)!.SetValue(scenario.Loot, true);
            InvokePrivate(scenario.Movement, "ProcessPostKillTargetRelease", CancellationToken.None);
            Assert.Empty(scenario.Movement.MouseEvents);
            typeof(LootSystem).GetField("_lootScanCameraOwned", PrivateInstance)!.SetValue(scenario.Loot, false);

            scenario.Movement.ReleaseTargetOnLeftUp = false;
            InvokePrivate(scenario.Movement, "ProcessPostKillTargetRelease", CancellationToken.None);
            SetPrivateField(scenario.Movement, "_postKillTargetReleaseRetryAtUtc", DateTime.UtcNow.AddMilliseconds(-1));
            InvokePrivate(scenario.Movement, "ProcessPostKillTargetRelease", CancellationToken.None);

            Assert.Equal(2, scenario.Movement.MouseEvents.Count(flag => flag == MouseOperations.MouseEventFlags.LeftDown));
            Assert.Equal(2, scenario.Movement.MouseEvents.Count(flag => flag == MouseOperations.MouseEventFlags.LeftUp));
            Assert.Equal(0, (int)typeof(MovementSystem)
                .GetField("_postKillTargetReleaseId", PrivateInstance)!.GetValue(scenario.Movement)!);
            Assert.True(scenario.Loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Pending, status.State);
            Assert.True(scenario.Movement.IsLootNavigationBlocked);
            Assert.Contains(scenario.Log, line => line.Contains("release failed target=16386", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(LootRequestState.Pending, "Pending")]
        [InlineData(LootRequestState.Running, "Running")]
        [InlineData(LootRequestState.Deferred, "Deferred")]
        [InlineData(LootRequestState.Completed, "Failed")]
        [InlineData(LootRequestState.Failed, "Failed")]
        [InlineData(LootRequestState.Cancelled, "Cancelled")]
        [InlineData(LootRequestState.Unavailable, "Failed")]
        public async Task NonVerifiedLootStatesNeverStartTheCloseFirstRetarget(
            LootRequestState requestState, string expectedBarrierPhase)
        {
            using var scenario = CreateRetargetScenario();
            InvokePrivate(scenario.Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            long requestId = scenario.Movement.LootBarrier.RequestId;
            Assert.True(requestId > 0);

            DriveMandatoryRequestState(scenario, requestId, requestState);
            InvokePrivate(scenario.Movement, "RefreshLootBarrier");
            Assert.Equal(expectedBarrierPhase, scenario.Movement.LootBarrier.Phase.ToString());

            // Even with a stale expectation armed, the guard must keep waiting and must
            // never consume the tick with a TAB.
            SetPrivateField(scenario.Movement, "_postKillRetargetRequestId", requestId);
            bool consumed = await InvokePrivateBoolAsync(
                scenario.Movement, "HandlePostKillRetargetRequestAsync", CancellationToken.None);

            Assert.True(consumed);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Equal("None", ReadPrivateField<object>(
                scenario.Movement, "_combatRetargetCameraStage").ToString());
            Assert.Equal(requestId, ReadPrivateField<long>(
                scenario.Movement, "_postKillRetargetRequestId"));
            Assert.True(scenario.Movement.IsLootNavigationBlocked);
            Assert.DoesNotContain(scenario.Log,
                line => line.Contains("starting close-first search", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SecondStageMonsterSelectionStopsTheLadderBeforeMid()
        {
            using var scenario = CreateRetargetScenario();
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");
            int presses = 0;
            scenario.Movement.OnTargetCycle = () =>
            {
                presses++;
                if (presses == 2)
                    scenario.Memory.TargetId = 16386;
            };

            Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));
            Assert.True(await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None));

            bool stageConsumed = await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.MoveAndAttackAndLoot, (short)17020, CancellationToken.None);

            Assert.False(stageConsumed);
            Assert.Equal(new[] { (short)16880, (short)16910 }, scenario.Movement.TargetCycleCameraDistances);
            Assert.Equal(2, scenario.Movement.TargetCyclePresses);
            Assert.Equal("None", ReadPrivateField<object>(
                scenario.Movement, "_combatRetargetCameraStage").ToString());
            Assert.Contains(scenario.Log,
                line => line.Contains("Monster selected targetId=16386 stage=Low", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ActiveMandatoryLootHoldsAnInFlightRetargetWithoutCameraOrTab()
        {
            bool groupingWasEnabled = MobGroupingRuntimeSettings.Enabled;
            try
            {
                MobGroupingRuntimeSettings.Enabled = false;
                using var scenario = CreateRetargetScenario();
                InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");
                InvokePrivate(scenario.Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
                long requestId = scenario.Movement.LootBarrier.RequestId;

                await scenario.Movement.Update(CancellationToken.None);

                Assert.True(scenario.Movement.IsLootNavigationBlocked);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
                Assert.Equal((short)17020, scenario.Memory.CameraDistance);
                Assert.Equal("VeryLowSearch", ReadPrivateField<object>(
                    scenario.Movement, "_combatRetargetCameraStage").ToString());

                RunMandatoryService(scenario.Loot);
                CompleteMandatory(scenario.Loot, requestId, ValidEmptyOutcome());

                await scenario.Movement.Update(CancellationToken.None);

                Assert.False(scenario.Movement.IsLootNavigationBlocked);
                Assert.Equal(new[] { (short)16880 }, scenario.Movement.TargetCycleCameraDistances);
            }
            finally
            {
                MobGroupingRuntimeSettings.Enabled = groupingWasEnabled;
            }
        }

        [Fact]
        public async Task TabAfterKillWithoutALootSystemBlocksNavigationInsteadOfTab()
        {
            bool groupingWasEnabled = MobGroupingRuntimeSettings.Enabled;
            try
            {
                MobGroupingRuntimeSettings.Enabled = false;
                using var scenario = CreateRetargetScenario();
                scenario.Movement.LootSystemRef = null;
                object handler = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)!
                    .GetValue(scenario.Movement)!;
                SetPrivateField(handler, "_wasAttacking", true);

                await scenario.Movement.Update(CancellationToken.None);

                Assert.True(scenario.Movement.IsLootNavigationBlocked);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.Contains(scenario.Log,
                    line => line.Contains("FATAL: MoveAndAttackAndLoot has no loot system", StringComparison.Ordinal));

                // Force the ordinary move-mode cycle interval to elapse: the failed
                // post-kill barrier must still suppress any wide-camera TAB.
                SetPrivateField(handler, "_lastMoveModeTabTime", DateTime.MinValue);
                await scenario.Movement.Update(CancellationToken.None);

                Assert.True(scenario.Movement.IsLootNavigationBlocked);
                Assert.Equal(0, scenario.Movement.TargetCyclePresses);
                Assert.DoesNotContain(scenario.Log,
                    line => line.Contains("[Key] TAB", StringComparison.Ordinal));
            }
            finally
            {
                MobGroupingRuntimeSettings.Enabled = groupingWasEnabled;
            }
        }

        [Fact]
        public void WaypointChangeClearsThePostKillRetargetContext()
        {
            using var scenario = CreateRetargetScenario();
            InvokePrivate(scenario.Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            SetPrivateField(scenario.Movement, "_postKillRetargetRequestId",
                scenario.Movement.LootBarrier.RequestId);
            SetPrivateField(scenario.Movement, "_postKillTargetReleaseId", 16386);
            SetPrivateField(scenario.Movement, "_postKillTargetReleaseAttempts", 1);
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");

            InvokePrivate(scenario.Movement, "ResetCombatStateForWaypointChange");

            Assert.Equal(0L, ReadPrivateField<long>(scenario.Movement, "_postKillRetargetRequestId"));
            Assert.Equal(0, ReadPrivateField<int>(scenario.Movement, "_postKillTargetReleaseId"));
            Assert.Equal(0, ReadPrivateField<int>(scenario.Movement, "_postKillTargetReleaseAttempts"));
            Assert.Equal("None", ReadPrivateField<object>(
                scenario.Movement, "_combatRetargetCameraStage").ToString());
        }

        [Fact]
        public void StopClearsThePostKillRetargetContext()
        {
            using var scenario = CreateRetargetScenario();
            InvokePrivate(scenario.Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            SetPrivateField(scenario.Movement, "_postKillRetargetRequestId",
                scenario.Movement.LootBarrier.RequestId);
            SetPrivateField(scenario.Movement, "_postKillTargetReleaseId", 16386);
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");

            scenario.Movement.Stop();

            Assert.Equal(0L, ReadPrivateField<long>(scenario.Movement, "_postKillRetargetRequestId"));
            Assert.Equal(0, ReadPrivateField<int>(scenario.Movement, "_postKillTargetReleaseId"));
            Assert.Equal("None", ReadPrivateField<object>(
                scenario.Movement, "_combatRetargetCameraStage").ToString());
        }

        [Fact]
        public async Task OnlyMoveModeCannotRunTheCloseFirstLadder()
        {
            using var scenario = CreateRetargetScenario(BotMode.OnlyMove);
            InvokePrivate(scenario.Movement, "StartCombatRetargetSearch");

            bool consumed = await InvokePrivateBoolAsync(scenario.Movement,
                "HandleCombatRetargetCameraStageAsync", BotMode.OnlyMove, (short)17020, CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Equal((short)17020, scenario.Memory.CameraDistance);
            Assert.Equal("None", ReadPrivateField<object>(
                scenario.Movement, "_combatRetargetCameraStage").ToString());
        }

        // ── Cancellation / focus loss ───────────────────────────────────────────

        [Fact]
        public void FocusLossDefersButNeverCompletesAStoppedLootTask()
        {
            using var loot = CreateLoot(_ => { });
            long id = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);

            var cancelAll = typeof(LootSystem).GetMethod("CancelAllLootWork", PrivateInstance);
            Assert.NotNull(cancelAll);
            cancelAll!.Invoke(loot, new object[] { "game window is not foreground", true });

            Assert.True(loot.TryGetMandatoryLootRequestStatus(id, out LootRequestStatus deferred));
            Assert.Equal(LootRequestState.Deferred, deferred.State);
            Assert.False(deferred.CameraVerified);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(id, LootBarrierReason.PostKill);
            barrier.ApplyStatus(deferred);
            Assert.True(barrier.IsBlockingNavigation);

            loot.Stop();
            Assert.True(loot.TryGetMandatoryLootRequestStatus(id, out LootRequestStatus cancelled));
            Assert.Equal(LootRequestState.Cancelled, cancelled.State);
            barrier.ApplyStatus(cancelled);
            Assert.True(barrier.IsBlockingNavigation);
        }
    }
}
