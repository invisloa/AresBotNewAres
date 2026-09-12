using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Guards the EXP/combat input-state invariant:
    /// a subsystem must never leave a synthetic key held after it stops owning execution.
    ///
    /// Background: MovementSystem holds the attack key (3) down across ticks while combat
    /// is active and only releases it on a later combat tick. When the EXP loop was
    /// interrupted (repot / cancellation / phase change) while 3 was held, the release
    /// tick never ran and PathRunnerService only released W/A/D — so key 3 leaked into
    /// the Repot phase and W movement stopped working in town. The fix funnels every
    /// termination path through MovementSystem.ReleaseCombatKeys (existing ownership
    /// flag, existing "[Key] 3 up" logging).
    ///
    /// Test strategy: the low-level input goes straight to user32, so these tests never
    /// synthesize a real key-DOWN. The "attack key currently held" state is simulated by
    /// setting the existing private ownership flag — exactly the state MovementSystem is
    /// in right after it logs "[Key] 3 hold (attack skill)". The tests then only ever
    /// inject the harmless key-UP under test while asserting state + log ordering.
    /// Game memory is stubbed (all reads fail), so MovementSystem.Update performs no
    /// input of its own and returns early on the failed position read.
    /// </summary>
    public sealed class CombatInputCleanupTests
    {
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

        private sealed class LogSink
        {
            private readonly object _gate = new object();
            private readonly List<string> _lines = new List<string>();

            public void Add(string message)
            {
                lock (_gate) _lines.Add(message);
            }

            public IReadOnlyList<string> Snapshot()
            {
                lock (_gate) return _lines.ToList();
            }

            public int CountContaining(string text) => Snapshot().Count(l => l.Contains(text));

            public int IndexOf(string text)
            {
                var snapshot = Snapshot();
                for (int i = 0; i < snapshot.Count; i++)
                    if (snapshot[i].Contains(text))
                        return i;
                return -1;
            }
        }

        /// <summary>
        /// Simulates the exact ownership state right after MovementSystem logs
        /// "[Key] 3 hold (attack skill)" (the flag is set immediately after the
        /// keybd_event down). Uses the existing flag — no parallel tracking.
        /// </summary>
        private static void SimulateAttackKeyHeld(MovementSystem movement)
        {
            var field = typeof(MovementSystem).GetField(
                "_isSkillThreeHeld", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new InvalidOperationException(
                    "MovementSystem._isSkillThreeHeld not found — the combat ownership flag was renamed; update the cleanup contract.");
            field.SetValue(movement, true);
        }

        private static MovementSystem CreateMovement(LogSink sink)
        {
            var memory = CreateStubMemory(sink.Add);
            var waypoints = new List<Waypoint>
            {
                new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.OnlyMove)
            };
            var movement = new MovementSystem(
                memory,
                sink.Add,
                targetX: 5000,
                targetY: 5000,
                precision: MovementPrecision.Medium,
                customPath: waypoints,
                initialMode: BotMode.OnlyMove,
                loopPath: true)
            {
                InternalRepotEnabled = false
            };
            return movement;
        }

        // ── Scenario 4 — cleanup when already released ──

        [Fact]
        public void ReleaseCombatKeys_WhenAlreadyReleased_IsIdempotentAndSilent()
        {
            var sink = new LogSink();
            var movement = CreateMovement(sink);

            Assert.False(movement.IsAttackKeyHeld);

            // Must not produce invalid state, must not log releases, must not throw.
            movement.ReleaseCombatKeys();
            movement.ReleaseCombatKeys();

            Assert.False(movement.IsAttackKeyHeld);
            Assert.Equal(0, sink.CountContaining("[Key] 3 up"));
        }

        // ── Scenario 2 — repot interrupts active attack ──

        [Fact]
        public void ReleaseCombatKeys_WhenAttackHeld_ReleasesOnceAndLogsKeyUp()
        {
            var sink = new LogSink();
            var movement = CreateMovement(sink);

            // Given: combat is active, attack key is currently held.
            SimulateAttackKeyHeld(movement);
            Assert.True(movement.IsAttackKeyHeld);

            // Then: the key is released through the existing release logging.
            movement.ReleaseCombatKeys();

            Assert.False(movement.IsAttackKeyHeld);
            Assert.Equal(1, sink.CountContaining("[Key] 3 up"));

            // Cleanup stays idempotent so the next combat cycle is unaffected.
            movement.ReleaseCombatKeys();
            Assert.False(movement.IsAttackKeyHeld);
            Assert.Equal(1, sink.CountContaining("[Key] 3 up"));
        }

        // ── Scenarios 2+3 — PathRunner termination (repot/cancellation) ordering ──

        [Fact]
        public async Task PathRunner_CancellationWhileAttackHeld_ReleasesBeforePathStopped()
        {
            var sink = new LogSink();
            var memory = CreateStubMemory(sink.Add);
            var runner = new PathRunnerService(memory, sink.Add);
            var waypoints = new List<Waypoint>
            {
                new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };

            using var cts = new CancellationTokenSource();
            Task<bool> runTask = runner.RunPathAsync(waypoints, loop: true, cts.Token);

            // Wait until the runner owns a movement instance (set synchronously on start).
            MovementSystem? movement = null;
            for (int i = 0; i < 250 && movement == null; i++)
            {
                movement = runner.CurrentMovement;
                if (movement == null)
                    await Task.Delay(20);
            }
            Assert.NotNull(movement);

            // Given: EXP is interrupted while the attack key is held.
            SimulateAttackKeyHeld(movement!);
            Assert.True(movement!.IsAttackKeyHeld);

            // When: the EXP loop is cancelled (repot condition / phase change).
            cts.Cancel();
            Task completed = await Task.WhenAny(runTask, Task.Delay(10_000));
            Assert.Same(runTask, completed);
            Assert.False(await runTask);

            // Then: the key is released before the path hands control back.
            Assert.False(movement.IsAttackKeyHeld);
            int keyUp = sink.IndexOf("[Key] 3 up");
            int stopped = sink.IndexOf("[PathRunner] Path stopped.");
            Assert.NotEqual(-1, keyUp);
            Assert.NotEqual(-1, stopped);
            Assert.True(keyUp < stopped);
        }

        // ── Workflow Stop path ──

        [Fact]
        public async Task PathRunner_Stop_ReleasesHeldAttackKey()
        {
            var sink = new LogSink();
            var memory = CreateStubMemory(sink.Add);
            var runner = new PathRunnerService(memory, sink.Add);
            var waypoints = new List<Waypoint>
            {
                new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };

            using var cts = new CancellationTokenSource();
            Task<bool> runTask = runner.RunPathAsync(waypoints, loop: true, cts.Token);

            MovementSystem? movement = null;
            for (int i = 0; i < 250 && movement == null; i++)
            {
                movement = runner.CurrentMovement;
                if (movement == null)
                    await Task.Delay(20);
            }
            Assert.NotNull(movement);

            SimulateAttackKeyHeld(movement!);

            // When: the workflow is stopped externally.
            runner.Stop();

            // Then: combat-owned keys are released immediately (and idempotently).
            Assert.False(movement!.IsAttackKeyHeld);
            Assert.Equal(1, sink.CountContaining("[Key] 3 up"));
            runner.Stop();
            Assert.Equal(1, sink.CountContaining("[Key] 3 up"));

            cts.Cancel();
            await Task.WhenAny(runTask, Task.Delay(10_000));
        }

        // ── Stop with no active movement must stay safe ──

        [Fact]
        public void PathRunner_Stop_WithoutActiveMovement_DoesNotThrow()
        {
            var sink = new LogSink();
            var memory = CreateStubMemory(sink.Add);
            var runner = new PathRunnerService(memory, sink.Add);

            var exception = Record.Exception(() => runner.Stop());
            Assert.Null(exception);
        }
    }
}
