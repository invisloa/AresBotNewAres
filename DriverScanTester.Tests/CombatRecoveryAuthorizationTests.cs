using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using static DriverScanTester.Services.LootSystem;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the combat-owned physical recovery authorization.
    ///
    /// Bug this guards: in MoveAndAttackAndLoot a post-kill mandatory loot request stays
    /// Deferred while combat holds a selected mob. That request keeps the navigation
    /// barrier active, which used to suppress BOTH <c>ApplyCameraBearing</c> and
    /// <c>StartMoving</c>. <c>RepositionAndRetryAttack</c> nevertheless waited the full
    /// 2500 ms and then TABbed into the same phantom target, so the character never
    /// physically moved (observed: StartMoving suppressed at 01:29:08.635, attack
    /// retried at 01:29:11.144, position unchanged). ReverseDiagonalRecovery (combat
    /// unstuck) failed the same way because its forward input is the StartMoving
    /// delegate.
    ///
    /// Correct behaviour: only a combat-owned recovery may drive camera + W, only for
    /// the exact Deferred PostKill request generation and only while a valid mob is
    /// selected. Ordinary waypoint navigation stays blocked, the obligation keeps its
    /// request id and stays Deferred, and a reposition only counts when real
    /// displacement was observed.
    ///
    /// Test strategy: movement key/TAB events and the game-foreground check are
    /// overridden in a test subclass, so no real key-down is injected into user32 (the
    /// established convention of this project's tests). Player position and the selected
    /// target come from a stubbed memory reader.
    /// </summary>
    public sealed class CombatRecoveryAuthorizationTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        // ── Stub memory ─────────────────────────────────────────────────────────

        private sealed class CombatMemoryStub
        {
            private const ulong PlayerPtrOffset = BotConstants.MemoryOffsets.PlayerPtr;
            private const ulong PlayerBase = 0x100000;

            public int TargetId = 5000;   // valid mob (< MaxMobTargetId)
            public byte Action = 39;      // attacking (non-idle)
            public Func<(short X, short Y)> Position = () => ((short)100, (short)100);

            public GameMemoryService Create(Action<string> log)
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;

                        if (address == PlayerPtrOffset)
                            return Fill(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                        if (address == PlayerBase + BotConstants.MemoryOffsets.TargetSelected)
                            return Fill(buffer, BitConverter.GetBytes(TargetId), out bytesRead);

                        if (address == PlayerBase + BotConstants.MemoryOffsets.CurrentAction)
                        {
                            buffer[0] = Action;
                            bytesRead = 1;
                            return true;
                        }

                        if (address == PlayerBase + BotConstants.MemoryOffsets.Animation1)
                            return Fill(buffer, BitConverter.GetBytes((short)0), out bytesRead);

                        if (address == PlayerBase + BotConstants.MemoryOffsets.X)
                        {
                            (short x, _) = Position();
                            return Fill(buffer, BitConverter.GetBytes(x), out bytesRead);
                        }

                        if (address == PlayerBase + BotConstants.MemoryOffsets.Y)
                        {
                            (_, short y) = Position();
                            return Fill(buffer, BitConverter.GetBytes(y), out bytesRead);
                        }

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

            private static bool Fill(byte[] buffer, byte[] data, out uint bytesRead)
            {
                Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                bytesRead = (uint)data.Length;
                return true;
            }
        }

        // ── Test movement system (records input instead of injecting it) ────────

        private sealed class TestMovementSystem : MovementSystem
        {
            public TestMovementSystem(GameMemoryService memory, Action<string> log, IEnumerable<Waypoint> path)
                : base(memory, log, 1000, 1000, customPath: path, loopPath: false)
            {
            }

            /// <summary>Simulated game focus state for the recovery focus guard.</summary>
            public bool Foreground { get; set; } = true;

            public int ForwardKeyDowns { get; private set; }
            public int ForwardKeyUps { get; private set; }
            public int TargetCyclePresses { get; private set; }

            protected override bool IsGameClientForegroundForRecovery() => Foreground;

            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
            {
                if (keyUp)
                    ForwardKeyUps++;
                else
                    ForwardKeyDowns++;
            }

            protected override void SendTargetCycleKey() => TargetCyclePresses++;
        }

        // ── Scenario fixture ───────────────────────────────────────────────────

        private sealed class Scenario : IDisposable
        {
            public CombatMemoryStub Stub { get; }
            public List<string> Log { get; } = new();
            public LootSystem Loot { get; }
            public TestMovementSystem Movement { get; }
            public long RequestId { get; private set; }

            public Scenario(BotMode mode = BotMode.MoveAndAttackAndLoot)
            {
                Stub = new CombatMemoryStub();
                GameMemoryService memory = Stub.Create(Log.Add);
                Loot = new LootSystem(memory, Log.Add);
                Movement = new TestMovementSystem(memory, Log.Add, new[]
                {
                    new Waypoint(1000, 1000, MovementPrecision.Medium, mode)
                })
                {
                    LootSystemRef = Loot,
                    InternalRepotEnabled = false
                };
            }

            /// <summary>Arms a real post-kill obligation through the production entry point.</summary>
            public void ArmPostKill()
            {
                InvokePrivate(Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
                RequestId = Movement.LootBarrier.RequestId;
            }

            /// <summary>Defers the armed obligation because combat holds a selected mob.</summary>
            public void DeferForCombat()
            {
                InvokePrivate(Loot, "ServiceMandatoryLootRequest", true);
                InvokePrivate(Movement, "RefreshLootBarrier");
            }

            public void RefreshBarrier() => InvokePrivate(Movement, "RefreshLootBarrier");

            public LootRequestStatus Status()
            {
                Assert.True(Loot.TryGetMandatoryLootRequestStatus(RequestId, out LootRequestStatus status));
                return status;
            }

            public bool ForwardKeyDownObserved => Movement.ForwardKeyDowns > 0;

            public void Dispose() => Loot.Dispose();
        }

        private static object? InvokePrivate(object target, string method, params object?[] args)
        {
            Type type = target is MovementSystem ? typeof(MovementSystem) : target.GetType();
            MethodInfo info = type.GetMethod(method, PrivateInstance)
                ?? throw new InvalidOperationException($"Method {method} not found on {type.Name}.");
            return info.Invoke(target, args);
        }

        private static Task StartReposition(MovementSystem movement, float x, float y, CancellationToken token)
        {
            var task = (Task?)InvokePrivate(movement, "RepositionAndRetryAttack", x, y, token);
            Assert.NotNull(task);
            return task!;
        }

        private static bool InvokeTryStartMoving(MovementSystem movement)
            => (bool)InvokePrivate(movement, "TryStartMoving")!;

        private static bool InvokeTryBeginCombatRecovery(MovementSystem movement, string reason)
            => (bool)InvokePrivate(movement, "TryBeginCombatRecoveryMovement", reason)!;

        private static bool BarrierHoldsInput(MovementSystem movement)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_lootNavigationBarrierHoldsInput", PrivateInstance)
                ?? throw new InvalidOperationException("barrier input field not found");
            return (bool)field.GetValue(movement)!;
        }

        private static bool IsMovingForward(MovementSystem movement)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance)
                ?? throw new InvalidOperationException("_isMovingForward not found");
            return (bool)field.GetValue(movement)!;
        }

        private static bool IsUnstuckRoutineActive(MovementSystem movement)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_isUnstuckRoutineActive", PrivateInstance)
                ?? throw new InvalidOperationException("_isUnstuckRoutineActive not found");
            return (bool)field.GetValue(movement)!;
        }

        private static bool IsReverseDiagonalActive(MovementSystem movement)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_reverseDiagonalRecovery", PrivateInstance)
                ?? throw new InvalidOperationException("_reverseDiagonalRecovery not found");
            object recovery = field.GetValue(movement)!;
            PropertyInfo property = recovery.GetType().GetProperty("IsActive")
                ?? throw new InvalidOperationException("IsActive not found");
            return (bool)property.GetValue(recovery)!;
        }

        private static int RepositionRetryCount(MovementSystem movement)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_repositionRetryCount", PrivateInstance)
                ?? throw new InvalidOperationException("_repositionRetryCount not found");
            return (int)field.GetValue(movement)!;
        }

        private static (float X, float Y)? LastRepositionPos(MovementSystem movement)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_lastRepositionPos", PrivateInstance)
                ?? throw new InvalidOperationException("_lastRepositionPos not found");
            return ((float X, float Y)?)field.GetValue(movement);
        }

        private static bool IsCombatRecoverySuspended(MovementSystem movement)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_combatRecoverySuspendedByLootHold", PrivateInstance)
                ?? throw new InvalidOperationException("_combatRecoverySuspendedByLootHold not found");
            return (bool)field.GetValue(movement)!;
        }

        /// <summary>
        /// Reads the combat handler's attack-episode flag. CombatHandler.ResetState sets it to
        /// false, so it proves whether a failed/aborted reposition reset the combat baseline.
        /// </summary>
        private static bool GetWasAttacking(MovementSystem movement)
        {
            FieldInfo handlerField = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)
                ?? throw new InvalidOperationException("_combatHandler not found");
            object handler = handlerField.GetValue(movement)!;
            FieldInfo field = typeof(CombatHandler).GetField("_wasAttacking", PrivateInstance)
                ?? throw new InvalidOperationException("_wasAttacking not found");
            return (bool)field.GetValue(handler)!;
        }

        private static void SetWasAttacking(MovementSystem movement, bool value)
        {
            FieldInfo handlerField = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)
                ?? throw new InvalidOperationException("_combatHandler not found");
            object handler = handlerField.GetValue(movement)!;
            FieldInfo field = typeof(CombatHandler).GetField("_wasAttacking", PrivateInstance)
                ?? throw new InvalidOperationException("_wasAttacking not found");
            field.SetValue(handler, value);
        }

        private static LootScanPassOutcome ValidEmptyOutcome()
            => new(LootScanPassResult.NoItem, LootScanPassResult.NoItem,
                LootScanPassResult.NoItem, CameraVerified: true, "test empty pass");

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.True(condition(), "condition was not reached in time");
        }

        // ── 1. Authorized recovery: camera + real W under the Deferred hold ─────

        [Fact]
        public async Task DeferredPostKillWithSelectedMobAuthorizesCameraAndForwardMovement()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.True(BarrierHoldsInput(scenario.Movement));
            Assert.True(scenario.Movement.IsLootNavigationBlocked);

            // Real displacement while W is held: the stub walks the player away.
            short step = 0;
            scenario.Stub.Position = () => ((short)(100 + (step += 2)), (short)100);

            await StartReposition(scenario.Movement, 100, 100, CancellationToken.None);

            Assert.True(scenario.Movement.ForwardKeyDowns > 0,
                "Authorized combat recovery must press W despite the loot navigation hold.");
            Assert.Contains(scenario.Log, l => l.Contains("[Camera] Apply brg=", StringComparison.Ordinal));
            Assert.Contains(scenario.Log, l => l.Contains("[CombatRecovery] Authorized physical recovery movement", StringComparison.Ordinal));
            Assert.Contains(scenario.Log, l => l.Contains("Reposition confirmed by", StringComparison.Ordinal));
            Assert.Equal(1, scenario.Movement.TargetCyclePresses);
            Assert.False(IsMovingForward(scenario.Movement));

            // The obligation keeps its id and stays Deferred — never completed by movement.
            LootRequestStatus status = scenario.Status();
            Assert.Equal(LootRequestState.Deferred, status.State);
            Assert.Equal(scenario.RequestId, status.RequestId);
            Assert.False(status.CameraVerified);
            Assert.True(BarrierHoldsInput(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
        }

        // ── 2. No displacement → no retry, no fake success ──────────────────────

        [Fact]
        public async Task PositionWithoutDisplacementNeverCountsAsAReposition()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            // Static position: W is accepted but the character never actually moves.
            scenario.Stub.Position = () => ((short)102, (short)61);

            await StartReposition(scenario.Movement, 102, 61, CancellationToken.None);

            Assert.True(scenario.Movement.ForwardKeyDowns > 0);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Contains(scenario.Log, l => l.Contains("Reposition failed — moved only", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Reposition confirmed by", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Retrying attack", StringComparison.Ordinal));
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
        }

        // ── 3. Denied input → no 2500 ms wait, no TAB ──────────────────────────

        [Fact]
        public async Task DeniedRecoveryInputDoesNotWaitOrRetarget()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            // No valid mob selected: combat recovery is not authorized.
            scenario.Stub.TargetId = 0;

            var stopwatch = Stopwatch.StartNew();
            await StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 1000,
                $"Denied recovery must not wait the 2500 ms reposition budget (took {stopwatch.ElapsedMilliseconds} ms).");
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Contains(scenario.Log, l => l.Contains("Reposition denied", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Retrying attack", StringComparison.Ordinal));
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.True(BarrierHoldsInput(scenario.Movement));
        }

        // ── 4. Pending / Running obligations never authorize movement ──────────

        [Fact]
        public void PendingOrRunningMandatoryLootBlocksForcedMovement()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();

            // Pending
            Assert.Equal(LootRequestState.Pending, scenario.Status().State);
            Assert.False(InvokeTryBeginCombatRecovery(scenario.Movement, "pending test"));
            Assert.False(InvokeTryStartMoving(scenario.Movement));
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);

            // Running
            scenario.Loot.NotifyNavigationHeld(scenario.RequestId);
            InvokePrivate(scenario.Loot, "ServiceMandatoryLootRequest", false);
            Assert.Equal(LootRequestState.Running, scenario.Status().State);

            scenario.RefreshBarrier();
            Assert.False(InvokeTryBeginCombatRecovery(scenario.Movement, "running test"));
            Assert.False(InvokeTryStartMoving(scenario.Movement));
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.False(scenario.Loot.TryBeginCombatRecoveryMovement(scenario.RequestId));
        }

        // ── 5. Deferred obligation resumes and completes after combat ──────────

        [Fact]
        public void StationaryLootCannotStartWhileCombatRecoveryHoldsTheCharacter()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            // Authorize through the production grant path (selected mob + Deferred PostKill).
            Assert.True(InvokeTryBeginCombatRecovery(scenario.Movement, "handoff test"));
            Assert.True(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));

            // Race window: the target disappears while the recovery still holds W.
            scenario.Stub.TargetId = 0;
            InvokePrivate(scenario.Loot, "ServiceMandatoryLootRequest", false);

            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.Equal(scenario.RequestId, scenario.Status().RequestId);
            Assert.False(scenario.Loot.IsLootCycleActive);
            Assert.False(scenario.Loot.IsScanActive);

            // Releasing the handoff lets the same deferred obligation resume normally.
            InvokePrivate(scenario.Movement, "EndCombatRecoveryMovement", "test release");
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));

            InvokePrivate(scenario.Loot, "ServiceMandatoryLootRequest", false);
            Assert.Equal(LootRequestState.Running, scenario.Status().State);
            Assert.Equal(scenario.RequestId, scenario.Status().RequestId);
        }

        [Fact]
        public async Task DeferredObligationStaysDeferredDuringRecoveryThenCompletesAfterCombat()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            short step = 0;
            scenario.Stub.Position = () => ((short)(100 + (step += 3)), (short)100);
            await StartReposition(scenario.Movement, 100, 100, CancellationToken.None);

            // Still the same deferred generation, camera never verified by movement.
            LootRequestStatus afterRecovery = scenario.Status();
            Assert.Equal(LootRequestState.Deferred, afterRecovery.State);
            Assert.Equal(scenario.RequestId, afterRecovery.RequestId);
            Assert.False(afterRecovery.CameraVerified);

            // Combat ends: the loot system may now run its stationary verified cycle.
            scenario.Stub.TargetId = 0;
            scenario.RefreshBarrier();
            InvokePrivate(scenario.Loot, "ServiceMandatoryLootRequest", false);

            Assert.Equal(LootRequestState.Running, scenario.Status().State);
            Assert.Equal(scenario.RequestId, scenario.Status().RequestId);

            InvokePrivate(scenario.Loot, "CompleteMandatoryLootRequest", scenario.RequestId, ValidEmptyOutcome());
            LootRequestStatus completed = scenario.Status();
            Assert.Equal(LootRequestState.Completed, completed.State);
            Assert.Equal(scenario.RequestId, completed.RequestId);
            Assert.True(completed.CameraVerified);

            scenario.RefreshBarrier();
            Assert.False(scenario.Movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(scenario.Movement));
        }

        // ── 6. Abort on target loss / focus loss / phase transition / cancel ──

        [Fact]
        public async Task RecoveryAbortsImmediatelyWhenTheTargetDisappears()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();
            scenario.Stub.Position = () => ((short)100, (short)100);

            var stopwatch = Stopwatch.StartNew();
            Task reposition = StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            await Task.Delay(150);
            scenario.Stub.TargetId = 0;
            await reposition;
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 2400,
                $"Target loss must abort the reposition before the 2500 ms budget (took {stopwatch.ElapsedMilliseconds} ms).");
            Assert.Contains(scenario.Log, l => l.Contains("Reposition aborted", StringComparison.Ordinal));
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        [Fact]
        public async Task RecoveryAbortsImmediatelyWhenFocusIsLost()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();
            scenario.Stub.Position = () => ((short)100, (short)100);

            var stopwatch = Stopwatch.StartNew();
            Task reposition = StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            await Task.Delay(150);
            scenario.Movement.Foreground = false;
            await reposition;
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 2400);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        [Fact]
        public async Task RecoveryAbortsWhenTheMandatoryRequestLeavesDeferred()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();
            scenario.Stub.Position = () => ((short)100, (short)100);

            Task reposition = StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            await WaitUntilAsync(() => scenario.Movement.ForwardKeyDowns > 0, 1000);

            scenario.Loot.CancelMandatoryLootRequest(scenario.RequestId, "stop");
            await reposition;

            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(LootRequestState.Cancelled, scenario.Status().State);
        }

        [Fact]
        public async Task RecoveryCancellationReleasesForwardInput()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();
            scenario.Stub.Position = () => ((short)100, (short)100);

            using var cancellation = new CancellationTokenSource();
            Task reposition = StartReposition(scenario.Movement, 100, 100, cancellation.Token);
            await WaitUntilAsync(() => scenario.Movement.ForwardKeyDowns > 0, 1000);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reposition);

            Assert.False(IsMovingForward(scenario.Movement));
            Assert.True(scenario.Movement.ForwardKeyUps > 0);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        // ── 7. Combat unstuck / ReverseDiagonal under the Deferred barrier ─────

        [Fact]
        public async Task CombatUnstuckReverseDiagonalRecoversUnderDeferredBarrier()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();
            Assert.True(BarrierHoldsInput(scenario.Movement));

            InvokePrivate(scenario.Movement, "StartCombatUnstuck", 100f, 100f);

            Assert.True(scenario.Movement.IsCombatRecoveryMovementAuthorized,
                "Combat unstuck must be authorized while a mob is selected and the post-kill request is Deferred.");
            Assert.True(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.True(IsUnstuckRoutineActive(scenario.Movement));

            // Drive the production hold path: camera settle (200 ms) → W → displacement.
            await Task.Delay(220);
            InvokePrivate(scenario.Movement, "TickCombatRecoveryUnderLootHold", 100f, 100f);
            Assert.True(scenario.Movement.ForwardKeyDowns > 0,
                "ReverseDiagonal must be able to press W under the Deferred barrier.");

            await Task.Delay(220);
            InvokePrivate(scenario.Movement, "TickCombatRecoveryUnderLootHold", 100f, 104f);

            await Task.Delay(320);
            InvokePrivate(scenario.Movement, "TickCombatRecoveryUnderLootHold", 100f, 104f);

            Assert.Contains(scenario.Log, l => l.Contains("[ReverseDiagonal] recovered under the mandatory loot hold", StringComparison.Ordinal));
            Assert.False(IsUnstuckRoutineActive(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.True(BarrierHoldsInput(scenario.Movement));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        [Fact]
        public async Task CombatUnstuckAbortsWhenTheTargetIsLostDuringRecovery()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            InvokePrivate(scenario.Movement, "StartCombatUnstuck", 100f, 100f);
            Assert.True(scenario.Movement.IsCombatRecoveryMovementAuthorized);

            await Task.Delay(220);
            InvokePrivate(scenario.Movement, "TickCombatRecoveryUnderLootHold", 100f, 100f);

            scenario.Stub.TargetId = 0;
            InvokePrivate(scenario.Movement, "TickCombatRecoveryUnderLootHold", 100f, 100f);

            Assert.Contains(scenario.Log, l => l.Contains("Recovery aborted", StringComparison.Ordinal));
            Assert.False(IsUnstuckRoutineActive(scenario.Movement));
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        // ── 8. No ordinary navigation leak while the barrier is active ─────────

        [Fact]
        public void OrdinaryWaypointInputStaysBlockedWithoutAnExplicitAuthorization()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            // A mob is selected and the request is Deferred, but no combat recovery is
            // running: ordinary navigation must still be fully blocked.
            Assert.True(scenario.Stub.TargetId > 0);
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);

            InvokePrivate(scenario.Movement, "StartMoving");
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);

            InvokePrivate(scenario.Movement, "ApplyCameraBearing", 90f);
            Assert.DoesNotContain(scenario.Log, l => l.Contains("[Camera] Apply brg=", StringComparison.Ordinal));
            Assert.False(IsMovingForward(scenario.Movement));

            Assert.True(BarrierHoldsInput(scenario.Movement));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        // ── 9. Unrestricted repositioning without a mandatory loot barrier ─────

        [Theory]
        [InlineData(BotMode.MoveAndAttack)]
        [InlineData(BotMode.MoveAndAttackAndLoot)]
        public async Task RepositionIsUnrestrictedWhenNoMandatoryLootRequestExists(BotMode mode)
        {
            using var scenario = new Scenario(mode);
            Assert.False(BarrierHoldsInput(scenario.Movement));
            Assert.False(scenario.Movement.IsLootNavigationBlocked);

            short step = 0;
            scenario.Stub.Position = () => ((short)(100 + (step += 2)), (short)100);
            SetWasAttacking(scenario.Movement, true);

            var stopwatch = Stopwatch.StartNew();
            await StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            stopwatch.Stop();

            // The full reposition budget is walked (the old regression aborted after the first
            // 50 ms poll because no combat-recovery authorization existed to lose).
            Assert.True(stopwatch.ElapsedMilliseconds >= BotConstants.Combat.CombatRepositionDurationMs - 250,
                $"Unrestricted repositioning must run the full budget (took {stopwatch.ElapsedMilliseconds} ms).");
            Assert.True(scenario.Movement.ForwardKeyDowns > 0);
            Assert.Contains(scenario.Log, l => l.Contains("Reposition confirmed by", StringComparison.Ordinal));
            Assert.Equal(1, scenario.Movement.TargetCyclePresses);
            Assert.False(GetWasAttacking(scenario.Movement), "A successful reposition resets the combat baseline for the retry.");
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
        }

        // ── 10. Denied authorization: combat unstuck is never activated ────────

        [Fact]
        public void DeniedAuthorizationNeverLeavesAnActiveUnstuckThatCannotAdvance()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            // No selected mob: the mandatory hold refuses the recovery authorization.
            scenario.Stub.TargetId = 0;

            InvokePrivate(scenario.Movement, "StartCombatUnstuck", 100f, 100f);

            Assert.False(IsReverseDiagonalActive(scenario.Movement));
            Assert.False(IsUnstuckRoutineActive(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
            Assert.Contains(scenario.Log, l => l.Contains("Combat unstuck not started (1/4)", StringComparison.Ordinal));
            Assert.False(IsCombatRecoverySuspended(scenario.Movement));
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
        }

        [Fact]
        public async Task FailedAuthorizationThenCombatUnstuckIsBoundedAndReleasesCleanly()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();
            scenario.Stub.TargetId = 0;

            // Denied reposition: no movement, no TAB, quick return.
            await StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);

            // Denied combat unstuck: never activates a recovery that cannot run.
            InvokePrivate(scenario.Movement, "StartCombatUnstuck", 100f, 100f);
            Assert.False(IsReverseDiagonalActive(scenario.Movement));
            Assert.False(IsUnstuckRoutineActive(scenario.Movement));

            // Bounded: the fourth denial trips the suspension instead of retrying forever.
            InvokePrivate(scenario.Movement, "StartCombatUnstuck", 100f, 100f);
            InvokePrivate(scenario.Movement, "StartCombatUnstuck", 100f, 100f);
            InvokePrivate(scenario.Movement, "StartCombatUnstuck", 100f, 100f);

            Assert.True(IsCombatRecoverySuspended(scenario.Movement));
            Assert.Contains(scenario.Log, l => l.Contains("combat unstuck cannot run under the mandatory loot hold", StringComparison.Ordinal));
            Assert.False(IsReverseDiagonalActive(scenario.Movement));
            Assert.False(IsUnstuckRoutineActive(scenario.Movement));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);

            // Suspended repositioning stays quiet: no W, no TAB.
            var stopwatch = Stopwatch.StartNew();
            await StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            Assert.True(stopwatch.ElapsedMilliseconds < 1000);
            Assert.Equal(0, scenario.Movement.ForwardKeyDowns);
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Contains(scenario.Log, l => l.Contains("Combat recovery movement suspended", StringComparison.Ordinal));

            // Once the obligation completes, the hold releases and the normal recovery chain
            // becomes available again.
            scenario.Loot.NotifyNavigationHeld(scenario.RequestId);
            InvokePrivate(scenario.Loot, "ServiceMandatoryLootRequest", false);
            Assert.Equal(LootRequestState.Running, scenario.Status().State);
            InvokePrivate(scenario.Loot, "CompleteMandatoryLootRequest", scenario.RequestId, ValidEmptyOutcome());
            scenario.RefreshBarrier();
            Assert.Equal(LootRequestState.Completed, scenario.Status().State);
            Assert.False(BarrierHoldsInput(scenario.Movement));

            InvokePrivate(scenario.Movement, "StartCombatUnstuck", 100f, 100f);
            Assert.False(IsCombatRecoverySuspended(scenario.Movement));
            Assert.True(IsReverseDiagonalActive(scenario.Movement));
            Assert.True(IsUnstuckRoutineActive(scenario.Movement));
        }

        // ── 11. Abort after real movement: never reset, never TAB ──────────────

        [Fact]
        public async Task TargetLossAfterMeaningfulMovementNeverResetsOrRetabs()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            var stopwatch = Stopwatch.StartNew();
            float maxMoved = 0f;
            scenario.Stub.Position = () =>
            {
                short x = (short)(100 + Math.Min(20, (int)(stopwatch.ElapsedMilliseconds / 40)));
                maxMoved = Math.Max(maxMoved, x - 100f);
                return (x, (short)100);
            };
            SetWasAttacking(scenario.Movement, true);

            Task reposition = StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            await Task.Delay(220);
            Assert.True(maxMoved > BotConstants.ReverseDiagonal.SuccessDisplacement,
                "the scenario must have produced meaningful movement before the target is lost");

            scenario.Stub.TargetId = 0;
            await reposition;

            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Contains(scenario.Log, l => l.Contains("Reposition aborted", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Reposition confirmed by", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Retrying attack", StringComparison.Ordinal));
            Assert.True(GetWasAttacking(scenario.Movement),
                "an aborted reposition must not reset the combat baseline");
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        [Fact]
        public async Task FocusLossAfterMeaningfulMovementNeverResetsOrRetabs()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            var stopwatch = Stopwatch.StartNew();
            float maxMoved = 0f;
            scenario.Stub.Position = () =>
            {
                short x = (short)(100 + Math.Min(20, (int)(stopwatch.ElapsedMilliseconds / 40)));
                maxMoved = Math.Max(maxMoved, x - 100f);
                return (x, (short)100);
            };
            SetWasAttacking(scenario.Movement, true);

            Task reposition = StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            await Task.Delay(220);
            Assert.True(maxMoved > BotConstants.ReverseDiagonal.SuccessDisplacement);

            scenario.Movement.Foreground = false;
            await reposition;

            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.True(GetWasAttacking(scenario.Movement));
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(LootRequestState.Deferred, scenario.Status().State);
        }

        [Fact]
        public async Task LootStateChangeAfterMeaningfulMovementNeverResetsOrRetabs()
        {
            using var scenario = new Scenario();
            scenario.ArmPostKill();
            scenario.DeferForCombat();

            var stopwatch = Stopwatch.StartNew();
            float maxMoved = 0f;
            scenario.Stub.Position = () =>
            {
                short x = (short)(100 + Math.Min(20, (int)(stopwatch.ElapsedMilliseconds / 40)));
                maxMoved = Math.Max(maxMoved, x - 100f);
                return (x, (short)100);
            };
            SetWasAttacking(scenario.Movement, true);

            Task reposition = StartReposition(scenario.Movement, 100, 100, CancellationToken.None);
            await Task.Delay(220);
            Assert.True(maxMoved > BotConstants.ReverseDiagonal.SuccessDisplacement);

            scenario.Loot.CancelMandatoryLootRequest(scenario.RequestId, "test phase change");
            await reposition;

            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.True(GetWasAttacking(scenario.Movement));
            Assert.False(IsMovingForward(scenario.Movement));
            Assert.False(scenario.Movement.IsCombatRecoveryMovementAuthorized);
            Assert.False(scenario.Loot.IsCombatRecoveryMovementActiveFor(scenario.RequestId));
            Assert.Equal(LootRequestState.Cancelled, scenario.Status().State);
        }

        // ── 12. Final position decides success, not the historical maximum ─────

        [Fact]
        public async Task DisplacementLostBeforeTheEndIsNotASuccessfulReposition()
        {
            using var scenario = new Scenario(BotMode.MoveAndAttack);

            var stopwatch = Stopwatch.StartNew();
            float maxMoved = 0f;
            scenario.Stub.Position = () =>
            {
                // Walk away for the first second, then return to the start position.
                bool away = stopwatch.ElapsedMilliseconds < 1000;
                short x = away ? (short)112 : (short)100;
                short y = away ? (short)100 : (short)61;
                maxMoved = Math.Max(maxMoved, (float)Math.Sqrt((x - 100) * (x - 100) + (y - 61) * (y - 61)));
                return (x, y);
            };
            SetWasAttacking(scenario.Movement, true);

            await StartReposition(scenario.Movement, 100, 61, CancellationToken.None);

            Assert.True(maxMoved > BotConstants.ReverseDiagonal.SuccessDisplacement,
                "the scenario must have walked beyond the displacement threshold before returning");
            Assert.Equal(0, scenario.Movement.TargetCyclePresses);
            Assert.Contains(scenario.Log, l => l.Contains("Reposition failed — moved only", StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Log, l => l.Contains("Retrying attack", StringComparison.Ordinal));
            Assert.True(GetWasAttacking(scenario.Movement));
            Assert.False(IsMovingForward(scenario.Movement));
        }

        // ── 13. Escalation accounting after one successful move ────────────────

        [Fact]
        public async Task StalledRecoveriesAfterOneSuccessfulMoveStillEscalate()
        {
            using var scenario = new Scenario(BotMode.MoveAndAttack);

            // Attempt 1 really moves the player (unrestricted, no loot barrier).
            scenario.Stub.Position = () => ((short)150, (short)100);
            await StartReposition(scenario.Movement, 100, 100, CancellationToken.None);

            Assert.Equal(1, RepositionRetryCount(scenario.Movement));
            Assert.Equal((100f, 100f), LastRepositionPos(scenario.Movement)!.Value);
            Assert.Contains(scenario.Log, l => l.Contains("Reposition confirmed by", StringComparison.Ordinal));

            // Attempt 2 starts from the new position and cannot move any further: the baseline
            // must advance, otherwise the old pre-move baseline keeps resetting the counter.
            await StartReposition(scenario.Movement, 150, 100, CancellationToken.None);
            Assert.Equal(1, RepositionRetryCount(scenario.Movement));
            Assert.Equal((150f, 100f), LastRepositionPos(scenario.Movement)!.Value);

            // Attempts 3 and 4 from the unchanged position accumulate the failure count.
            await StartReposition(scenario.Movement, 150, 100, CancellationToken.None);
            Assert.Equal(2, RepositionRetryCount(scenario.Movement));

            await StartReposition(scenario.Movement, 150, 100, CancellationToken.None);
            Assert.Equal(3, RepositionRetryCount(scenario.Movement));

            // Attempt 5 reaches the bounded escalation and starts the standard unstuck.
            await StartReposition(scenario.Movement, 150, 100, CancellationToken.None);
            Assert.Contains(scenario.Log, l => l.Contains("reposition attempts without escaping the broken target", StringComparison.Ordinal));
            Assert.Equal(0, RepositionRetryCount(scenario.Movement));
            Assert.Null(LastRepositionPos(scenario.Movement));
            Assert.True(IsReverseDiagonalActive(scenario.Movement));
            Assert.True(IsUnstuckRoutineActive(scenario.Movement));
        }
    }
}
