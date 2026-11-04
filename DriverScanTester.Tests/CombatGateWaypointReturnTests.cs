using System;
using System.Collections.Generic;
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
    /// Regression coverage for the CombatGate waypoint return recovery
    /// (<c>TickCombatGateWaypointReturnUnderLootBarrier</c>).
    ///
    /// Production incident (2026-11-04 02:06): MoveAndAttackAndLoot reached a state where the
    /// CombatGate suppressed attacking (Manhattan distance 34 &gt; AttackDisengageDistance 33), the
    /// Deferred post-kill loot obligation blocked ordinary navigation and the still-selected
    /// monster prevented the mandatory stationary scan from running. No W was ever requested, so
    /// neither normal movement-stuck recovery nor combat unstuck could fire: the character stood
    /// still at ~(198,132) indefinitely ("Overshoot ... attack disabled until waypoint is reached"
    /// followed by endless <c>Fwd:False Unst:False Mob:True</c> ticks).
    ///
    /// These tests drive the real <see cref="MovementSystem.Update"/> pipeline with a deterministic
    /// memory stub, the real loot-request lifecycle and captured (never injected) keyboard input, so
    /// the fix is proven end to end instead of by poking private flags. A dedicated test class (in
    /// its own file of the same test project) keeps the production-pipeline harness isolated from
    /// the narrower recovery-authorization tests in CombatRecoveryAuthorizationTests.cs.
    /// </summary>
    public sealed class CombatGateWaypointReturnTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        // Production numbers: waypoint (164,139), player (198,132), AttackDisengageDistance 33.
        private const float ProductionWaypointX = 164f;
        private const float ProductionWaypointY = 139f;
        private const float ProductionPlayerX = 198f;
        private const float ProductionPlayerY = 132f;
        private const short ProductionAttackDisengageDistance = 33;
        private const int ProductionMobTargetId = 16346;

        // ── Deterministic game memory ───────────────────────────────────────────

        /// <summary>
        /// Minimal but coherent game-memory simulation: one stable map, configurable player
        /// position/city flag/action, one selected target id and a writable camera. Unknown offsets
        /// read as false so production reads see exactly the same "value unavailable" evidence.
        /// </summary>
        private sealed class ReturnMemoryStub
        {
            public const ulong ModuleBase = 0x100000;
            private const ulong PlayerBase = 0x200000;
            private const ulong CameraBase = 0x300000;
            private const ulong PlayerRange = 0x4000;
            private const ulong CameraRange = 0x800;

            public float X = ProductionPlayerX;
            public float Y = ProductionPlayerY;
            public int Map = 1;
            public bool InCity;
            public byte Action = 25;
            public float CameraAngle;
            public short CameraDistance = BotConstants.Camera.DefaultDistanceLock;
            public int TargetId = ProductionMobTargetId;

            public GameMemoryService Create(Action<string> log)
                => new(0, Read, Write, ModuleBase, 8, log);

            private bool Read(uint pid, ulong address, byte[] buffer, out uint bytesRead)
            {
                bytesRead = 0;
                if (buffer.Length == 0)
                    return false;

                if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                    return Copy(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);
                if (address == ModuleBase + BotConstants.MemoryOffsets.CameraPtr)
                    return Copy(buffer, BitConverter.GetBytes(CameraBase), out bytesRead);

                if (address >= PlayerBase && address < PlayerBase + PlayerRange)
                {
                    ulong offset = address - PlayerBase;
                    byte[] value = offset switch
                    {
                        BotConstants.MemoryOffsets.X => BitConverter.GetBytes((short)MathF.Round(X)),
                        BotConstants.MemoryOffsets.Y => BitConverter.GetBytes((short)MathF.Round(Y)),
                        BotConstants.MemoryOffsets.IsInCity => BitConverter.GetBytes(InCity ? 1 : 0),
                        BotConstants.MemoryOffsets.CurrentMap => BitConverter.GetBytes(Map),
                        BotConstants.MemoryOffsets.CurrentAction => new[] { Action },
                        BotConstants.MemoryOffsets.TargetSelected => BitConverter.GetBytes(TargetId),
                        BotConstants.MemoryOffsets.Animation1 => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Animation2 => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Hp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.Mana => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.MaxHp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.MaxMp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.AttackSpeed1 => BitConverter.GetBytes((short)0),
                        BotConstants.MemoryOffsets.AttackSpeed2 => BitConverter.GetBytes((short)0),
                        _ => Array.Empty<byte>()
                    };
                    return Copy(buffer, value, out bytesRead);
                }

                if (address >= CameraBase && address < CameraBase + CameraRange)
                {
                    ulong offset = address - CameraBase;
                    byte[] value = offset switch
                    {
                        BotConstants.MemoryOffsets.CameraAngle => BitConverter.GetBytes(CameraAngle),
                        BotConstants.MemoryOffsets.CameraDistance => BitConverter.GetBytes(CameraDistance),
                        _ => Array.Empty<byte>()
                    };
                    return Copy(buffer, value, out bytesRead);
                }

                return false;
            }

            private bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = (uint)buffer.Length;
                if (address >= CameraBase && address < CameraBase + CameraRange)
                {
                    ulong offset = address - CameraBase;
                    if (offset == BotConstants.MemoryOffsets.CameraAngle && buffer.Length == 4)
                        CameraAngle = BitConverter.ToSingle(buffer, 0);
                    else if (offset == BotConstants.MemoryOffsets.CameraDistance && buffer.Length == 2)
                        CameraDistance = BitConverter.ToInt16(buffer, 0);
                }
                return true;
            }

            private static bool Copy(byte[] buffer, byte[] value, out uint bytesRead)
            {
                Array.Clear(buffer, 0, buffer.Length);
                int count = Math.Min(buffer.Length, value.Length);
                if (count > 0)
                    Array.Copy(value, buffer, count);
                bytesRead = (uint)buffer.Length;
                return true;
            }
        }

        // ── MovementSystem that records input instead of injecting it ───────────

        private sealed class ReturnTestMovementSystem : MovementSystem
        {
            private readonly ReturnMemoryStub _stub;

            public ReturnTestMovementSystem(
                GameMemoryService memory,
                Action<string> log,
                IEnumerable<Waypoint> path,
                ReturnMemoryStub stub,
                StubCombatPlayerPresenceScanner presence)
                : base(memory, log, ProductionWaypointX, ProductionWaypointY, customPath: path, loopPath: false,
                    steeringMode: MovementSteeringMode.DirectCamera,
                    targetSafety: CombatSafetyFixtures.WithPresence(() => stub.TargetId, presence))
            {
                _stub = stub;
            }

            public bool Foreground { get; set; } = true;
            public bool ReleaseTargetOnLeftUp { get; set; } = true;
            public int ClientLeft { get; set; } = 448;
            public int ClientTop { get; set; } = 131;
            public int ClientWidth { get; set; } = 1024;
            public int ClientHeight { get; set; } = 768;
            private int _cursorX;
            private int _cursorY;
            private bool _cursorKnown;
            public List<string> MouseActions { get; } = new();
            public List<string> InputOrder { get; } = new();

            public int ForwardKeyDowns { get; private set; }
            public int ForwardKeyUps { get; private set; }
            public int TargetCyclePresses { get; private set; }
            public int SkillThreeDowns { get; private set; }
            public int FailureScreenshotCalls { get; private set; }

            protected override bool IsGameClientForegroundForRecovery() => Foreground;

            protected override bool TryResolveCombatGateClientArea(
                out nint hwnd, out int clientScreenLeft, out int clientScreenTop,
                out int clientWidth, out int clientHeight)
            {
                hwnd = Foreground ? (nint)1 : nint.Zero;
                clientScreenLeft = ClientLeft;
                clientScreenTop = ClientTop;
                clientWidth = ClientWidth;
                clientHeight = ClientHeight;
                return Foreground && ClientWidth > 0 && ClientHeight > 0;
            }

            protected override bool IsCombatGateClientForeground(nint hwnd) => Foreground && hwnd != nint.Zero;

            protected override void MoveCombatGateMouseCursor(int screenX, int screenY)
            {
                MouseActions.Add($"Move:{screenX},{screenY}");
                InputOrder.Add($"MouseMove:{screenX},{screenY}");
                _cursorX = screenX;
                _cursorY = screenY;
                _cursorKnown = true;
            }

            protected override bool TryGetCombatGateCursorPosition(out int screenX, out int screenY)
            {
                screenX = _cursorX;
                screenY = _cursorY;
                return _cursorKnown;
            }

            protected override void SendCombatGateMouseEvent(MouseOperations.MouseEventFlags flags)
            {
                MouseActions.Add(flags.ToString());
                InputOrder.Add($"Mouse:{flags}");
                if (flags == MouseOperations.MouseEventFlags.LeftUp && ReleaseTargetOnLeftUp)
                    _stub.TargetId = 0;
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
            {
                InputOrder.Add(keyUp ? "WUp" : "WDown");
                if (keyUp)
                    ForwardKeyUps++;
                else
                    ForwardKeyDowns++;
            }

            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }

            protected override void SendTurnKeyEvent(bool leftA, bool keyUp) { }

            protected override void SendSkillThreeKeyEvent(bool keyUp)
            {
                InputOrder.Add(keyUp ? "AttackUp" : "AttackDown");
                if (!keyUp)
                    SkillThreeDowns++;
            }

            protected override void SendTargetCycleKey() => TargetCyclePresses++;

            // No real desktop capture in regression tests.
            protected override void CaptureStuckScreenshot() { }

            protected override void CaptureCombatGateReturnFailureScreenshot() => FailureScreenshotCalls++;
        }

        // ── Scenario ────────────────────────────────────────────────────────────

        private sealed class ReturnScenario : IDisposable
        {
            public ReturnMemoryStub Stub { get; }
            public List<string> Log { get; } = new();
            public LootSystem Loot { get; }
            public ReturnTestMovementSystem Movement { get; }
            public long RequestId { get; private set; }

            public ReturnScenario(
                float playerX = ProductionPlayerX,
                float playerY = ProductionPlayerY,
                short attackDisengageDistance = ProductionAttackDisengageDistance,
                BotMode mode = BotMode.MoveAndAttackAndLoot,
                float waypointX = ProductionWaypointX,
                float waypointY = ProductionWaypointY,
                int targetId = ProductionMobTargetId)
            {
                Stub = new ReturnMemoryStub { X = playerX, Y = playerY, TargetId = targetId };
                GameMemoryService memory = Stub.Create(Log.Add);
                Loot = new LootSystem(memory, Log.Add);
                var presence = new StubCombatPlayerPresenceScanner();
                var path = new[]
                {
                    new Waypoint(waypointX, waypointY, MovementPrecision.Medium, mode,
                        attackDisengageDistance: attackDisengageDistance)
                };

                Movement = new ReturnTestMovementSystem(memory, Log.Add, path, Stub, presence)
                {
                    LootSystemRef = Loot,
                    InternalRepotEnabled = false
                };

                // The incident route was already walking this leg; the initial route-resync
                // optimisation (unrelated to this regression) must not replace the waypoint.
                SetField(Movement, "_initialResyncDone", true);
            }

            /// <summary>Arms a real post-kill obligation through the production entry point.</summary>
            public void ArmPostKill()
            {
                Invoke(Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
                RequestId = Movement.LootBarrier.RequestId;
                Assert.True(RequestId > 0, "the post-kill obligation must be armed");
            }

            /// <summary>Arms a travel-candidate verification obligation (production reason).</summary>
            public void ArmTravelCandidate()
            {
                Invoke(Movement, "ArmMandatoryLootBarrier", LootBarrierReason.TravelCandidateVerification);
                RequestId = Movement.LootBarrier.RequestId;
                Assert.True(RequestId > 0);
            }

            /// <summary>Defers the armed obligation because combat holds a selected mob.</summary>
            public void DeferForCombat()
            {
                Invoke(Loot, "ServiceMandatoryLootRequest", true);
                Invoke(Movement, "RefreshLootBarrier");
            }

            public Task TickAsync() => Movement.Update(CancellationToken.None);

            public LootRequestStatus Status()
            {
                Assert.True(Loot.TryGetMandatoryLootRequestStatus(RequestId, out LootRequestStatus status));
                return status;
            }

            public bool ReturnActive => GetBoolField(Movement, "_combatGateReturnActive");

            public long ReturnRequestId => (long)GetField(Movement, "_combatGateReturnRequestId")!;

            public void Dispose() => Loot.Dispose();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static object? Invoke(object target, string method, params object?[] args)
        {
            Type type = target is MovementSystem ? typeof(MovementSystem) : target.GetType();
            MethodInfo info = type.GetMethod(method, PrivateInstance)
                ?? throw new InvalidOperationException($"Method {method} not found on {type.Name}.");
            return info.Invoke(target, args);
        }

        private static object? GetField(object target, string field)
        {
            FieldInfo info = typeof(MovementSystem).GetField(field, PrivateInstance)
                ?? throw new InvalidOperationException($"Field {field} not found.");
            return info.GetValue(target);
        }

        private static void SetField(object target, string field, object? value)
        {
            FieldInfo info = typeof(MovementSystem).GetField(field, PrivateInstance)
                ?? throw new InvalidOperationException($"Field {field} not found.");
            info.SetValue(target, value);
        }

        private static bool GetBoolField(object target, string field) => (bool)GetField(target, field)!;

        private static DateTime GetDateTimeField(object target, string field)
            => (DateTime)GetField(target, field)!;

        private static int QueueCount(MovementSystem movement)
            => ((Queue<Waypoint>)GetField(movement, "_waypoints")!).Count;

        /// <summary>Forces the dedicated return's progress anchor into the past (timing window).</summary>
        private static void AgeReturnProgressAnchor(MovementSystem movement, int seconds)
            => SetField(movement, "_combatGateReturnLastProgressAt", DateTime.UtcNow.AddSeconds(-seconds));

        /// <summary>Advances the existing reverse-diagonal recovery one stage per tick.</summary>
        private static void AgeReverseDiagonalStage(MovementSystem movement)
        {
            object recovery = GetField(movement, "_reverseDiagonalRecovery")!;
            typeof(ReverseDiagonalRecovery)
                .GetField("_stageStartTime", PrivateInstance)!
                .SetValue(recovery, DateTime.Now.AddSeconds(-5));
        }

        private static bool IsReverseDiagonalActive(MovementSystem movement)
        {
            object recovery = GetField(movement, "_reverseDiagonalRecovery")!;
            return (bool)recovery.GetType().GetProperty("IsActive")!.GetValue(recovery)!;
        }

        private static LootScanPassOutcome ValidEmptyOutcome()
            => new(LootScanPassResult.NoItem, LootScanPassResult.NoItem,
                LootScanPassResult.NoItem, CameraVerified: true, "test empty pass");

        private static async Task TickUntilAsync(
            ReturnScenario scenario,
            Func<bool> condition,
            int timeoutMs = 6000,
            int tickDelayMs = 30)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(tickDelayMs);
                await scenario.TickAsync();
            }
            Assert.True(condition(), "condition was not reached in time");
        }

        // ── A. Overshoot releases the selected target before navigation ────────

        [Fact]
        public async Task OvershootReleasesAttackAndClicksDynamicClientCenterBeforeW()
        {
            using var scenario = new ReturnScenario(
                playerX: 124f, playerY: 175f, mode: BotMode.MoveAndAttackAndLoot,
                waypointX: 139f, waypointY: 156f, targetId: 16401);
            scenario.ArmPostKill();
            scenario.DeferForCombat();
            Invoke(scenario.Movement, "HoldSkillThree");
            Assert.True(scenario.Movement.IsAttackKeyHeld);

            await scenario.TickAsync();

            Assert.Contains(scenario.Log, l => l.Contains("[CombatGate] Overshoot d:", StringComparison.Ordinal) &&
                                               l.Contains(" > 33", StringComparison.Ordinal));
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGate] Overshoot detected", StringComparison.Ordinal));
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGate] Target before release: 16401", StringComparison.Ordinal));
            Assert.Equal(new[] { "Move:960,515", "LeftDown", "LeftUp" }, scenario.Movement.MouseActions);
            Assert.Equal(0, scenario.Stub.TargetId);
            Assert.False(scenario.Movement.IsAttackKeyHeld);
            Assert.Contains(scenario.Log, l => l.Contains("Target release result: Cleared", StringComparison.Ordinal));

            int leftUpIndex = scenario.Movement.InputOrder.IndexOf("Mouse:LeftUp");
            int forwardDownIndex = scenario.Movement.InputOrder.IndexOf("WDown");
            Assert.True(leftUpIndex >= 0 && forwardDownIndex > leftUpIndex,
                "the left-button release must complete before route movement starts");
            Assert.True(scenario.Movement.InputOrder.IndexOf("AttackUp") <
                        scenario.Movement.InputOrder.IndexOf("MouseMove:960,515"),
                "the held attack skill must be released before the target click");
        }

        [Fact]
        public async Task NoOvershootLeavesCombatAndMouseInputUnchanged()
        {
            using var scenario = new ReturnScenario(
                playerX: 125f, playerY: 175f, mode: BotMode.MoveAndAttack,
                waypointX: 139f, waypointY: 156f, targetId: 16401);

            await scenario.TickAsync();

            Assert.Empty(scenario.Movement.MouseActions);
            Assert.Equal(16401, scenario.Stub.TargetId);
            Assert.Contains(scenario.Log, l => l.Contains("Combat: Attack", StringComparison.Ordinal));
            Assert.True(scenario.Movement.SkillThreeDowns > 0);
        }

        [Fact]
        public async Task OvershootUsesTheCurrentClientRectangleRatherThanFixedCoordinates()
        {
            using var scenario = new ReturnScenario(
                playerX: 124f, playerY: 175f, mode: BotMode.MoveAndAttack,
                waypointX: 139f, waypointY: 156f, targetId: 16401);
            scenario.Movement.ClientLeft = 701;
            scenario.Movement.ClientTop = 88;
            scenario.Movement.ClientWidth = 803;
            scenario.Movement.ClientHeight = 607;

            await scenario.TickAsync();

            Assert.Equal(new[] { "Move:1102,391", "LeftDown", "LeftUp" }, scenario.Movement.MouseActions);
            Assert.Contains(scenario.Log, l => l.Contains("client=(401,303) screen=(1102,391)", StringComparison.Ordinal));
        }

        [Fact]
        public async Task LostForegroundDefersTargetClickAndMovement()
        {
            using var scenario = new ReturnScenario(
                playerX: 124f, playerY: 175f, mode: BotMode.MoveAndAttack,
                waypointX: 139f, waypointY: 156f, targetId: 16401);
            scenario.Movement.Foreground = false;

            await scenario.TickAsync();

            Assert.Empty(scenario.Movement.MouseActions);
            Assert.Equal(16401, scenario.Stub.TargetId);
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.Contains(scenario.Log, l => l.Contains("Target release deferred", StringComparison.Ordinal));

            scenario.Movement.Stop();
            Assert.Empty(scenario.Movement.MouseActions);
            Assert.False(scenario.Movement.IsAttackKeyHeld);
            Assert.Equal(0, (int)GetField(scenario.Movement, "_combatGateSuppressedTargetId")!);
        }

        // ── B. Production deadlock: the pipeline now requests authorized W ──────

        [Fact]
        public async Task ProductionDeadlockNowRequestsAuthorizedWaypointReturnMovement()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            DateTime started = DateTime.UtcNow;
            await scenario.TickAsync();

            // The CombatGate still suppresses the attack (unchanged behaviour).
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGate] Overshoot detected", StringComparison.Ordinal));
            Assert.Equal(new[] { "Move:960,515", "LeftDown", "LeftUp" }, scenario.Movement.MouseActions);
            Assert.Equal(0, scenario.Stub.TargetId);

            // The dedicated return takes the existing combat-recovery handoff for THIS request.
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] START", StringComparison.Ordinal));
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] Authorization granted", StringComparison.Ordinal));
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] W accepted", StringComparison.Ordinal));
            await scenario.TickAsync();
            Assert.Equal(1, scenario.Movement.MouseActions.Count(action => action == "LeftDown"));
            Assert.Equal(1, scenario.Movement.MouseActions.Count(action => action == "LeftUp"));
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Equal(0, scenario.Movement.SkillThreeDowns);
            Assert.Single(scenario.Log.Where(l => l.Contains("[CombatGateReturn] START", StringComparison.Ordinal)));
            Assert.True(scenario.ReturnActive);
            Assert.Equal(scenario.RequestId, scenario.ReturnRequestId);
            Assert.True(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.True(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));

            // The character is no longer permanently idle: real forward input is requested.
            Assert.True(scenario.Movement.ForwardKeyDowns > 0,
                "the deadlocked pipeline must request forward movement instead of standing still");
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.Contains(scenario.Log, l => l.Contains("[Camera] Apply brg=", StringComparison.Ordinal));

            // The obligation and the waypoint are untouched: no fake completion, no advancement.
            LootRequestStatus status = scenario.Status();
            Assert.Equal(LootRequestState.Deferred, status.State);
            Assert.Equal(scenario.RequestId, status.RequestId);
            Assert.False(status.CameraVerified);
            Assert.Equal(1, QueueCount(scenario.Movement));
            Assert.True(GetBoolField(scenario.Movement, "_lootNavigationBarrierHoldsInput"));
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));

            // Acts on the same tick instead of waiting a budget out.
            Assert.True((DateTime.UtcNow - started).TotalMilliseconds < 2000,
                "the return must act on the same tick, not after a long delay");
        }

        // ── B. Successful return to the waypoint ────────────────────────────────

        [Fact]
        public async Task ReturnStopsAtTheWaypointBoundaryAndCombatResumesAfterwards()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));

            // Real approach: still outside the reach threshold, but closer (progress).
            scenario.Stub.X = 170f;
            scenario.Stub.Y = 140f;
            await scenario.TickAsync();
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] PROGRESS", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));

            // Inside both completion conditions (Euclidean <= reach threshold AND
            // Manhattan <= attack-disengage - margin).
            scenario.Stub.X = 165f;
            scenario.Stub.Y = 138f;
            await scenario.TickAsync();

            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] REACHED", StringComparison.Ordinal));
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));
            Assert.True(scenario.Movement.ForwardKeyUps > 0, "W must be released on arrival");
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.False(scenario.ReturnActive);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.False(GetBoolField(scenario.Movement, "_attackSuppressedForCurrentWaypoint"));

            // No premature waypoint consumption and no fake loot completion.
            Assert.Equal(1, QueueCount(scenario.Movement));
            LootRequestStatus status = scenario.Status();
            Assert.Equal(LootRequestState.Deferred, status.State);
            Assert.Equal(scenario.RequestId, status.RequestId);
            Assert.False(status.CameraVerified);

            // The disengaged monster remains suppressed after arrival. A later selection is
            // released once instead of restarting the same attack/return oscillation.
            scenario.Stub.TargetId = ProductionMobTargetId;
            await scenario.TickAsync();
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Combat: Attack", StringComparison.Ordinal));
            Assert.Equal(2, scenario.Movement.MouseActions.Count(action => action == "LeftDown"));
            Assert.Equal(2, scenario.Movement.MouseActions.Count(action => action == "LeftUp"));
            Assert.False(scenario.ReturnActive, "suppression must not start another return for the same target");
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized,
                "releasing a reselected suppressed target must not strand a second loot handoff");
            Assert.Contains(scenario.Log, l => l.Contains("Target 16346 skipped: disengagement suppression active", StringComparison.Ordinal));

            await scenario.TickAsync();
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Equal(2, scenario.Movement.MouseActions.Count(action => action == "LeftDown"));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGate] TAB suppressed", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SuppressionDoesNotBlockAnotherConfirmedMonsterAfterRecovery()
        {
            using var scenario = new ReturnScenario(
                playerX: 124f, playerY: 175f, mode: BotMode.MoveAndAttack,
                waypointX: 139f, waypointY: 156f, targetId: 16401);

            await scenario.TickAsync();
            Assert.Equal(0, scenario.Stub.TargetId);
            Assert.Equal(1, scenario.Movement.MouseActions.Count(action => action == "LeftDown"));

            scenario.Stub.X = 139f;
            scenario.Stub.Y = 156f;
            await scenario.TickAsync();
            await scenario.TickAsync();
            scenario.Stub.TargetId = 20001;
            await scenario.TickAsync();

            Assert.True(scenario.Log.Any(l => l.Contains("Combat: Attack", StringComparison.Ordinal)),
                string.Join(Environment.NewLine, scenario.Log));
            Assert.True(scenario.Movement.SkillThreeDowns > 0);
            Assert.Equal(1, scenario.Movement.MouseActions.Count(action => action == "LeftDown"));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Target 20001 skipped", StringComparison.Ordinal));
        }

        // ── C. No movement: bounded stall, diagnostics, existing recovery ───────

        [Fact]
        public async Task StaticPositionStallsWithinTheWindowAndStartsTheExistingRecovery()
        {
            using var scenario = new ReturnScenario();
            scenario.Movement.ReleaseTargetOnLeftUp = false;
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await TickUntilAsync(scenario, () => scenario.ReturnActive);
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));
            DateTime returnStarted = GetDateTimeField(scenario.Movement, "_combatGateReturnStartedAt");
            Assert.Equal(2, scenario.Movement.MouseActions.Count(action => action == "LeftDown"));
            Assert.Contains(scenario.Log, l => l.Contains("Target release FAILED", StringComparison.Ordinal));

            await TickUntilAsync(scenario, () => scenario.Log.Any(l => l.Contains("[CombatGateReturn] STALLED", StringComparison.Ordinal)));

            double elapsed = (DateTime.UtcNow - returnStarted).TotalMilliseconds;
            Assert.True(elapsed >= BotConstants.CombatGateReturn.StallTimeoutMs - 250,
                $"the stall must not fire before the {BotConstants.CombatGateReturn.StallTimeoutMs}ms window (took {elapsed:F0}ms)");
            Assert.True(elapsed <= BotConstants.CombatGateReturn.StallTimeoutMs + 2000,
                $"the stall must fire within the window plus normal test tolerance (took {elapsed:F0}ms)");

            Assert.True(scenario.Movement.FailureScreenshotCalls > 0, "a failed return must capture diagnostics");
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.False(scenario.ReturnActive);
            Assert.Equal(0L, scenario.ReturnRequestId);
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] UNSTUCK", StringComparison.Ordinal));

            // The existing reverse-diagonal recovery took over — no invented escape algorithm.
            Assert.True(GetBoolField(scenario.Movement, "_isUnstuckRoutineActive"));
            Assert.True(IsReverseDiagonalActive(scenario.Movement));

            // Never a fake movement success or a fake loot completion.
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.Equal(scenario.RequestId, scenario.Status().RequestId);
        }

        // ── D. Sideways displacement is not progress ────────────────────────────

        [Fact]
        public async Task SidewaysDisplacementDoesNotResetTheStallWindow()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            DateTime returnStarted = GetDateTimeField(scenario.Movement, "_combatGateReturnStartedAt");

            // Coordinates change, distance to the waypoint does not improve (37.5 > 34.7).
            scenario.Stub.X = 190f;
            scenario.Stub.Y = 166f;

            await TickUntilAsync(scenario, () => scenario.Log.Any(l => l.Contains("[CombatGateReturn] STALLED", StringComparison.Ordinal)));

            double elapsed = (DateTime.UtcNow - returnStarted).TotalMilliseconds;
            Assert.True(elapsed >= BotConstants.CombatGateReturn.StallTimeoutMs - 250,
                $"sideways movement must not reset the timeout (stalled after {elapsed:F0}ms)");
            Assert.True(elapsed <= BotConstants.CombatGateReturn.StallTimeoutMs + 2000);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] PROGRESS", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        // ── E. Target disappears ────────────────────────────────────────────────

        [Fact]
        public async Task ClearedTargetDoesNotAbortTheAuthorizedWaypointReturn()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.Equal(0, scenario.Stub.TargetId);

            await scenario.TickAsync();

            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] ABORT reason=TargetLost", StringComparison.Ordinal));
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.True(scenario.ReturnActive);
            Assert.True(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.True(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));

            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        // ── F. Target becomes another player ────────────────────────────────────

        [Fact]
        public async Task TargetBecomingAnotherPlayerStopsTheReturnWithoutAnyAttack()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));

            // Player-id domain (> MaxMobTargetId) — confirmed other player, never attackable.
            scenario.Stub.TargetId = BotConstants.Combat.MaxMobTargetId + 500;
            await scenario.TickAsync();

            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] ABORT reason=TargetRejected", StringComparison.Ordinal));
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.False(scenario.ReturnActive);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(0, scenario.Movement.SkillThreeDowns);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Combat: Attack", StringComparison.Ordinal));

            // CombatGateReturn never TABs while its waypoint recovery owns the tick.
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);

            // No unsafe continuation on later ticks either.
            int pressesAfterAbort = scenario.Movement.TargetCyclePresses;
            await scenario.TickAsync();
            Assert.Equal(pressesAfterAbort, scenario.Movement.TargetCyclePresses);
            Assert.False(scenario.ReturnActive);
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        // ── G. Lost game focus ──────────────────────────────────────────────────

        [Fact]
        public async Task LostForegroundStopsTheReturnAndKeepsTheObligation()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));

            scenario.Movement.Foreground = false;
            await scenario.TickAsync();

            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] ABORT reason=AuthorizationLost", StringComparison.Ordinal));
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.True(scenario.Movement.ForwardKeyUps > 0);
            Assert.False(scenario.ReturnActive);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));
        }

        // ── H. Request generation change ────────────────────────────────────────

        [Fact]
        public async Task RequestGenerationChangeInvalidatesTheOldAuthorization()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            long oldRequestId = scenario.RequestId;
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));

            scenario.Loot.CancelMandatoryLootRequest(oldRequestId, "test generation change");
            await scenario.TickAsync();

            Assert.False(scenario.ReturnActive);
            Assert.Equal(0L, scenario.ReturnRequestId);
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(oldRequestId));
            Assert.Equal(LootRequestState.Cancelled, scenario.Status().State);

            // A brand-new obligation starts a NEW, correctly bound authorization — the old
            // generation can never move again and no fake completion exists.
            Invoke(scenario.Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            long newRequestId = scenario.Movement.LootBarrier.RequestId;
            Assert.NotEqual(oldRequestId, newRequestId);
            Invoke(scenario.Loot, "ServiceMandatoryLootRequest", true);
            Invoke(scenario.Movement, "RefreshLootBarrier");

            await scenario.TickAsync();
            Assert.True(scenario.ReturnActive);
            Assert.Equal(newRequestId, scenario.ReturnRequestId);
            Assert.Contains(scenario.Log, l => l.Contains($"[CombatGateReturn] Authorization granted request={newRequestId}", StringComparison.Ordinal));
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.True(scenario.Loot.TryGetMandatoryLootRequestStatus(newRequestId, out LootRequestStatus newStatus));
            Assert.Equal(LootRequestState.Deferred, newStatus.State);
            Assert.Equal(newRequestId, newStatus.RequestId);
            // The old generation stays cancelled and can never be completed by movement.
            Assert.Equal(LootRequestState.Cancelled, scenario.Status().State);
        }

        // ── I. Pending / Running obligations never authorize return movement ────

        [Fact]
        public async Task PendingOrRunningLootNeverStartsReturnMovement()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();

            // Pending.
            await scenario.TickAsync();
            Assert.Equal(LootRequestState.Pending, scenario.Status().State);
            Assert.False(scenario.ReturnActive);
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] START", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[Camera] Apply brg=", StringComparison.Ordinal));

            // Running: the stationary verification owns the camera.
            scenario.Loot.NotifyNavigationHeld(scenario.RequestId);
            Invoke(scenario.Loot, "ServiceMandatoryLootRequest", false);
            Assert.Equal(LootRequestState.Running, scenario.Status().State);

            await scenario.TickAsync();
            await scenario.TickAsync();

            Assert.False(scenario.ReturnActive);
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] START", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[Camera] Apply brg=", StringComparison.Ordinal));
            Assert.True(scenario.Movement.IsLootNavigationBlocked);
        }

        // ── J. Only a camera-verified completion releases navigation ────────────

        [Fact]
        public async Task OnlyACameraVerifiedCompletionReleasesMandatoryNavigation()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            // The return completes, but movement alone never releases the barrier.
            await scenario.TickAsync();
            scenario.Stub.X = 165f;
            scenario.Stub.Y = 138f;
            await scenario.TickAsync();
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));
            Assert.True(scenario.Movement.IsLootNavigationBlocked);
            Assert.True(GetBoolField(scenario.Movement, "_lootNavigationBarrierHoldsInput"));

            // Combat ends: the same generation runs the authoritative stationary verification.
            scenario.Stub.TargetId = 0;
            scenario.Loot.NotifyNavigationHeld(scenario.RequestId);
            Invoke(scenario.Loot, "ServiceMandatoryLootRequest", false);
            Assert.Equal(LootRequestState.Running, scenario.Status().State);
            await scenario.TickAsync();
            Assert.True(scenario.Movement.IsLootNavigationBlocked);

            // Only the camera-verified empty pass completes the obligation and releases navigation.
            Invoke(scenario.Loot, "CompleteMandatoryLootRequest", scenario.RequestId, ValidEmptyOutcome());
            await scenario.TickAsync();

            LootRequestStatus status = scenario.Status();
            Assert.Equal(LootRequestState.Completed, status.State);
            Assert.True(status.CameraVerified);
            Assert.False(scenario.Movement.IsLootNavigationBlocked);
            Assert.False(GetBoolField(scenario.Movement, "_lootNavigationBarrierHoldsInput"));
            Assert.Contains(scenario.Log, l => l.Contains("[LootBarrier] Completed", StringComparison.Ordinal));

            // Ordinary navigation resumes and consumes the (now reachable) waypoint.
            await scenario.TickAsync();
            await scenario.TickAsync();
            Assert.Equal(0, QueueCount(scenario.Movement));
        }

        // ── K. Automatic loot disabled ──────────────────────────────────────────

        [Fact]
        public async Task DisabledAutomaticLootNeverStartsTheReturn()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            scenario.Movement.DisableAllLootOverride = true;
            await scenario.TickAsync();
            await scenario.TickAsync();

            Assert.False(scenario.ReturnActive);
            Assert.Equal(new[] { "Move:960,515", "LeftDown", "LeftUp" }, scenario.Movement.MouseActions);
            Assert.True(scenario.Movement.ForwardKeyDowns > 0,
                "with mandatory loot disabled, ordinary waypoint navigation proceeds after target release");
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] START", StringComparison.Ordinal));
            Assert.False(scenario.Movement.IsLootNavigationBlocked);
            Assert.False(GetBoolField(scenario.Movement, "_lootNavigationBarrierHoldsInput"));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
        }

        // ── L. The new authorization is not a general loot-barrier bypass ───────

        [Fact]
        public async Task OrdinaryNavigationStaysBlockedWithoutTheExactCombatGateCondition()
        {
            // Inside the attack-disengage boundary: CombatGate is NOT active, so the mandatory
            // hold must keep blocking ordinary waypoint movement exactly as before.
            using var scenario = new ReturnScenario(playerX: 184f, playerY: 139f);
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            await scenario.TickAsync();

            Assert.False(scenario.ReturnActive);
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.False(GetBoolField(scenario.Movement, "_attackSuppressedForCurrentWaypoint"));
            Assert.True(scenario.Movement.IsLootNavigationBlocked);
            Assert.True(GetBoolField(scenario.Movement, "_lootNavigationBarrierHoldsInput"));
            Assert.Equal(1, QueueCount(scenario.Movement));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        [Fact]
        public async Task TravelCandidateObligationNeverStartsTheReturn()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmTravelCandidate();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            await scenario.TickAsync();

            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.Equal(LootBarrierReason.TravelCandidateVerification, scenario.Status().Reason);
            Assert.True(GetBoolField(scenario.Movement, "_attackSuppressedForCurrentWaypoint"),
                "the scenario must exercise the CombatGate suppression");
            Assert.False(scenario.ReturnActive);
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] START", StringComparison.Ordinal));
            Assert.True(scenario.Movement.IsLootNavigationBlocked);
        }

        // ── L2. Unobtainable handoff: diagnosed once, then bounded escalation ──

        [Fact]
        public async Task TargetReleaseWaitsWhileLootOwnsTheCursorThenStartsRecovery()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            // A running scan owns the cursor. The target click must wait rather than race loot,
            // and neither the deferred obligation nor waypoint recovery may be faked as complete.
            FieldInfo lootStateField = typeof(LootSystem).GetField("_lootState", PrivateInstance)!;
            object scanState = Enum.Parse(lootStateField.FieldType, "Scan");
            lootStateField.SetValue(scenario.Loot, scanState);

            await scenario.TickAsync();
            await scenario.TickAsync();

            Assert.Empty(scenario.Movement.MouseActions);
            Assert.Contains(scenario.Log, l => l.Contains("Target release deferred: mouse cursor is owned by the loot workflow", StringComparison.Ordinal));
            Assert.False(scenario.ReturnActive);
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.Equal(ProductionMobTargetId, scenario.Stub.TargetId);
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);

            lootStateField.SetValue(scenario.Loot, Enum.Parse(lootStateField.FieldType, "Idle"));
            await scenario.TickAsync();

            Assert.Equal(new[] { "Move:960,515", "LeftDown", "LeftUp" }, scenario.Movement.MouseActions);
            Assert.Equal(0, scenario.Stub.TargetId);
            Assert.True(scenario.ReturnActive);
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.False(scenario.Status().CameraVerified);
        }

        // ── M. Repeated failures escalate instead of looping forever ────────────

        [Fact]
        public async Task RepeatedReturnFailuresEscalateThroughTheExistingNavigationFailurePath()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            for (int attempt = 1; attempt <= BotConstants.CombatGateReturn.MaximumFailedAttempts; attempt++)
            {
                await scenario.TickAsync();
                Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"),
                    $"attempt {attempt} must request forward movement");

                AgeReturnProgressAnchor(scenario.Movement, 5);
                await scenario.TickAsync();

                Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] STALLED", StringComparison.Ordinal));
                Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"),
                    $"attempt {attempt} must release W");
                Assert.True(scenario.Movement.FailureScreenshotCalls >= attempt,
                    "every failed attempt must capture diagnostics");
                Assert.Equal(LootRequestState.Deferred, scenario.Status().State);

                // Let the existing bounded recovery finish before the next attempt.
                int guard = 0;
                while (GetBoolField(scenario.Movement, "_isUnstuckRoutineActive") && guard++ < 30)
                {
                    AgeReverseDiagonalStage(scenario.Movement);
                    await scenario.TickAsync();
                    Assert.False(GetBoolField(scenario.Movement, "_isMovingForward") && !IsReverseDiagonalActive(scenario.Movement),
                        "forward input must never stay held by a stopped recovery");
                }

                Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
                Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
                Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            }

            // The established escalation was reached instead of retrying forever.
            Assert.True(scenario.Movement.IsNavigationFailureEscalationRequested);
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] ABORT reason=RepeatedFailures", StringComparison.Ordinal));
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));

            // The obligation was never completed, cancelled or satisfied by a timeout.
            LootRequestStatus status = scenario.Status();
            Assert.NotEqual(LootRequestState.Completed, status.State);
            Assert.False(status.CameraVerified);
        }

        // ── N. Stop / map change cleanup ────────────────────────────────────────

        [Fact]
        public async Task ManualStopCancelsTheReturnAndReleasesAllInput()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));

            scenario.Movement.Stop();

            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] ABORT reason=movement system stopped", StringComparison.Ordinal));
            Assert.False(scenario.ReturnActive);
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.True(scenario.Movement.ForwardKeyUps > 0);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.NotEqual(LootRequestState.Completed, scenario.Status().State);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));
        }

        [Fact]
        public async Task CancellationReleasesReturnInputAndHandoff()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => scenario.Movement.Update(cancellation.Token));

            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] ABORT reason=operation cancelled", StringComparison.Ordinal));
            Assert.False(scenario.ReturnActive);
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.True(scenario.Movement.ForwardKeyUps > 0);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.NotEqual(LootRequestState.Completed, scenario.Status().State);
        }

        [Fact]
        public async Task MapChangeCancelsTheReturnAndReleasesAllInput()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            await scenario.TickAsync();
            Assert.True(GetBoolField(scenario.Movement, "_isMovingForward"));

            scenario.Stub.Map = 2;
            await scenario.TickAsync();

            // The old return was cancelled by the existing full route reset before anything else
            // (its W and handoff were released on the spot). The same tick may then re-evaluate the
            // still-Deferred obligation in the NEW route context, which is legitimate.
            Assert.Contains(scenario.Log, l => l.Contains("[CombatGateReturn] ABORT reason=route reset: map-change", StringComparison.Ordinal));
            Assert.True(scenario.Movement.ForwardKeyUps > 0, "the old return's W must be released by the reset");
            Assert.NotEqual(LootRequestState.Completed, scenario.Status().State);

            // A full stop on the new map leaves no return input or handoff behind.
            scenario.Movement.Stop();
            Assert.False(scenario.ReturnActive);
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[CombatGateReturn] COMPLETE", StringComparison.Ordinal));
        }

        // ── O. Pre-fix mechanics: the barrier alone never produced movement ─────

        [Fact]
        public void WithoutAuthorizationTheLootBarrierStillRefusesForwardInput()
        {
            using var scenario = new ReturnScenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            Assert.True(GetBoolField(scenario.Movement, "_lootNavigationBarrierHoldsInput"));
            Assert.False(scenario.ReturnActive);

            // The original failure mode: ordinary StartMoving is suppressed by the barrier.
            Invoke(scenario.Movement, "StartMoving");
            Assert.False(GetBoolField(scenario.Movement, "_isMovingForward"));
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.Contains(scenario.Log, l => l.Contains("StartMoving suppressed", StringComparison.Ordinal));
        }
    }
}
