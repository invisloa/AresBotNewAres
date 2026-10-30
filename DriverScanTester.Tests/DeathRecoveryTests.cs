using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for recoverable character death (2026-10-29 incident): a confirmed
    /// HP-zero death must NOT stop the whole bot through the connection-failure path. It must
    /// release inputs, keep the coordinator alive, wait the configured time, verify revival,
    /// teleport to town through the existing mechanism, verify arrival and restart the WHOLE
    /// active profile from its first action.
    ///
    /// Time is controllable through <see cref="BotWorkflowCoordinator.DeathRecoveryWaitMs"/> and
    /// friends, so the real five-minute wait is never performed in tests.
    /// </summary>
    public sealed class DeathRecoveryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class FakeMemory
        {
            public const ulong ModuleBase = 0x100000;
            public const ulong PlayerBase = 0x200000;
            public const ulong CameraBase = 0x300000;

            public int Map = 35;
            public short X = 100;
            public short Y = 100;
            public bool InCity;
            public short Hp = 500;
            public short MaxHp = 1000;
            public short Mana = 500;
            public short MaxMana = 1000;
            public int Target = 0;
            public bool FailPointer;
            public short VerticalLock = BotConstants.Camera.StationaryVerticalLock;

            public GameMemoryService CreateMemory(Action<string> log)
                => new(0, Read, Write, ModuleBase, 8, log);

            private bool Read(uint pid, ulong address, byte[] buffer, out uint bytesRead)
            {
                bytesRead = 0;

                if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                {
                    if (FailPointer)
                        return false;
                    return Copy(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);
                }

                if (address == ModuleBase + BotConstants.MemoryOffsets.CameraPtr)
                    return Copy(buffer, BitConverter.GetBytes(CameraBase), out bytesRead);

                if (address == CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset)
                    return Copy(buffer, BitConverter.GetBytes(VerticalLock), out bytesRead);

                if (address >= PlayerBase && address < PlayerBase + 0x2000)
                {
                    ulong offset = address - PlayerBase;
                    return offset switch
                    {
                        BotConstants.MemoryOffsets.CurrentMap =>
                            Copy(buffer, BitConverter.GetBytes(Map), out bytesRead),
                        BotConstants.MemoryOffsets.X =>
                            Copy(buffer, BitConverter.GetBytes(X), out bytesRead),
                        BotConstants.MemoryOffsets.Y =>
                            Copy(buffer, BitConverter.GetBytes(Y), out bytesRead),
                        BotConstants.MemoryOffsets.IsInCity =>
                            Copy(buffer, BitConverter.GetBytes(InCity ? 1 : 0), out bytesRead),
                        BotConstants.MemoryOffsets.Hp =>
                            Copy(buffer, BitConverter.GetBytes(Hp), out bytesRead),
                        BotConstants.MemoryOffsets.MaxHp =>
                            Copy(buffer, BitConverter.GetBytes(MaxHp), out bytesRead),
                        BotConstants.MemoryOffsets.Mana =>
                            Copy(buffer, BitConverter.GetBytes(Mana), out bytesRead),
                        BotConstants.MemoryOffsets.MaxMp =>
                            Copy(buffer, BitConverter.GetBytes(MaxMana), out bytesRead),
                        BotConstants.MemoryOffsets.TargetSelected =>
                            Copy(buffer, BitConverter.GetBytes(Target), out bytesRead),
                        BotConstants.MemoryOffsets.Animation1 =>
                            Copy(buffer, BitConverter.GetBytes(0), out bytesRead),
                        _ => false
                    };
                }

                return false;
            }

            private bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = 0;
                if (address == CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset &&
                    buffer.Length >= 2)
                {
                    VerticalLock = BitConverter.ToInt16(buffer, 0);
                    bytesWritten = 2;
                    return true;
                }
                return false;
            }

            private static bool Copy(byte[] destination, byte[] value, out uint bytesRead)
            {
                bytesRead = 0;
                if (value.Length < destination.Length)
                    return false;
                Buffer.BlockCopy(value, 0, destination, 0, destination.Length);
                bytesRead = (uint)destination.Length;
                return true;
            }
        }

        private sealed class Rig
        {
            public List<string> Log { get; } = new();
            public FakeMemory Memory { get; } = new();
            public BotWorkflowCoordinator Coordinator { get; set; } = null!;
            public int Teleports;
            public bool TeleportReturnsTrue = true;
            public bool TeleportSetsCity = true;
            public DateTime? FirstTeleportAtUtc;

            public IReadOnlyList<string> Snapshot()
            {
                lock (Log) return Log.ToList();
            }

            public int Count(string text) => Snapshot().Count(line => line.Contains(text));
        }

        private static PositionSyncOptions FastOptions() => new()
        {
            MapConfirmationTimeoutMs = 600,
            MapStableReadCount = 2,
            PollMs = 5,
            SettleTimeoutMs = 200,
            WRefreshSettleTimeoutMs = 200,
            WRefreshPollMs = 5,
            MaxWRefreshAttempts = 2,
            WRefreshRetryDelayMs = 5,
            MaxTransitionRestarts = 3,
            NudgeKeyDownMs = 1
        };

        private static Rig CreateRig(BotProfile profile)
        {
            var rig = new Rig();
            Action<string> log = line =>
            {
                lock (rig.Log) rig.Log.Add(line);
            };

            GameMemoryService memoryService = rig.Memory.CreateMemory(log);
            var pathLoader = new SavedPathLoader(log);
            var pathRunner = new PathRunnerService(
                memoryService,
                log,
                enableWaypointSpecialRecoveries: false,
                steeringMode: MovementSteeringMode.DirectCamera);
            var positionSync = new PositionSyncService(
                memoryService,
                log,
                () => { },
                _ =>
                {
                    // Deterministic mini-W: the simulated game publishes a moved position.
                    rig.Memory.X = (short)(rig.Memory.X + 1);
                    return Task.CompletedTask;
                },
                FastOptions());
            var itemSeller = new ItemSellerService(memoryService, log);
            var operationContext = new OperationContext(
                memoryService,
                pathRunner,
                itemSeller,
                profile,
                log,
                () => { },
                positionSync);
            var operationRunner = new OperationRunnerService(operationContext, log);
            var coordinator = new BotWorkflowCoordinator(
                memoryService,
                new RepotSystem(memoryService, log),
                new RepotDetectorService(log),
                pathLoader,
                pathRunner,
                operationRunner,
                profile,
                log,
                () => { },
                positionSync)
            {
                TownTeleportOverride = _ =>
                {
                    rig.Teleports++;
                    if (rig.FirstTeleportAtUtc == null)
                        rig.FirstTeleportAtUtc = DateTime.UtcNow;
                    if (rig.TeleportSetsCity)
                        rig.Memory.InCity = true;
                    return Task.FromResult(rig.TeleportReturnsTrue);
                },
                DeathRecoveryWaitMs = 150,
                DeathRecoveryRevivalPollMs = 10,
                DeathRecoveryRevivalRecheckWaitMs = 20,
                DeathRecoveryTownTeleportRetryDelayMs = 10
            };

            rig.Coordinator = coordinator;
            return rig;
        }

        private static BotProfile Profile() => new()
        {
            Name = "DeathRecoveryProfile",
            TeleportKey = 6,
            FlowSteps = new List<BotFlowStep>
            {
                new()
                {
                    Type = BotFlowStepType.Repot,
                    StartMapNumber = 35,
                    RepotPaths = new List<BotRouteStep>
                    {
                        new() { PathFile = "death-unused-repot.json", StartMapNumber = 35 }
                    }
                },
                new()
                {
                    Type = BotFlowStepType.Path,
                    StartMapNumber = 35,
                    Routes = new List<BotRouteStep>
                    {
                        new() { PathFile = "death-unused-travel.json", StartMapNumber = 35 }
                    }
                },
                new()
                {
                    Type = BotFlowStepType.ExpLoop,
                    StartMapNumber = 36,
                    Routes = new List<BotRouteStep>
                    {
                        new() { PathFile = "death-unused-exp.json", StartMapNumber = 36 }
                    }
                }
            }
        };

        private static void MarkRunning(BotWorkflowCoordinator coordinator, int flowIndex)
        {
            typeof(BotWorkflowCoordinator).GetField("_isRunning", PrivateInstance)!
                .SetValue(coordinator, true);
            typeof(BotWorkflowCoordinator).GetField("_flowIndex", PrivateInstance)!
                .SetValue(coordinator, flowIndex);
            typeof(BotWorkflowCoordinator).GetField("_cts", PrivateInstance)!
                .SetValue(coordinator, new CancellationTokenSource());
        }

        private static int FlowIndex(BotWorkflowCoordinator coordinator)
            => (int)typeof(BotWorkflowCoordinator).GetField("_flowIndex", PrivateInstance)!
                .GetValue(coordinator)!;

        private static Task RunRecoveryAsync(BotWorkflowCoordinator coordinator)
        {
            var cts = (CancellationTokenSource)typeof(BotWorkflowCoordinator)
                .GetField("_cts", PrivateInstance)!.GetValue(coordinator)!;
            return (Task)typeof(BotWorkflowCoordinator)
                .GetMethod("RunDeathRecoveryAsync", PrivateInstance)!
                .Invoke(coordinator, new object[] { cts.Token })!;
        }

        private static BotPhase Phase(BotWorkflowCoordinator coordinator) => coordinator.CurrentPhase;

        [Fact]
        public async Task ConfirmedDeathRunsOneRecoveryAndKeepsTheWorkflowAlive()
        {
            var rig = CreateRig(Profile());
            // The character already revived by the time recovery is processed (the 2026-10-29
            // death was confirmed at HP=0; the wait exists for the game's revival).
            rig.Memory.Hp = 800;
            MarkRunning(rig.Coordinator, flowIndex: 2);

            rig.Coordinator.NotifyPlayerDeathDetected();
            rig.Coordinator.NotifyPlayerDeathDetected();
            rig.Coordinator.NotifyPlayerDeathDetected();

            Assert.True(rig.Coordinator.IsDeathRecoveryRequested);
            Assert.True(rig.Coordinator.IsRunning);
            Assert.Equal(1, rig.Count("[DeathRecovery] Death confirmed."));
            Assert.Equal(1, rig.Count("[DeathRecovery] Active actions released."));

            await RunRecoveryAsync(rig.Coordinator);

            Assert.Equal(1, rig.Count("[DeathRecovery] Waiting"));
            Assert.Equal(1, rig.Teleports);
            Assert.NotNull(rig.FirstTeleportAtUtc);
            Assert.Contains(rig.Snapshot(), line => line.Contains("[DeathRecovery] Wait completed."));
            Assert.Contains(rig.Snapshot(), line => line.Contains("[DeathRecovery] Town arrival verified."));
            Assert.Contains(rig.Snapshot(), line => line.Contains("reset to its first action (1/3:"));
            Assert.Contains(rig.Snapshot(), line => line.Contains("[DeathRecovery] Recovery complete"));
            Assert.Equal(0, FlowIndex(rig.Coordinator));
            Assert.False(rig.Coordinator.IsDeathRecoveryRequested);
            Assert.Equal(BotPhase.Idle, Phase(rig.Coordinator));
            // Startup (user-start) map detection must never run during automatic recovery.
            Assert.DoesNotContain(rig.Snapshot(), line => line.Contains("[Startup]"));
        }

        [Fact]
        public async Task NoTownTeleportHappensBeforeTheWaitCompletes()
        {
            var rig = CreateRig(Profile());
            rig.Coordinator.DeathRecoveryWaitMs = 400;
            rig.Memory.Hp = 800;
            MarkRunning(rig.Coordinator, flowIndex: 1);

            DateTime started = DateTime.UtcNow;
            rig.Coordinator.NotifyPlayerDeathDetected();
            await RunRecoveryAsync(rig.Coordinator);

            Assert.NotNull(rig.FirstTeleportAtUtc);
            Assert.True((rig.FirstTeleportAtUtc!.Value - started).TotalMilliseconds >= 380,
                $"town teleport must not start during the death wait (elapsed {(rig.FirstTeleportAtUtc!.Value - started).TotalMilliseconds:F0} ms)");
        }

        [Fact]
        public async Task UnverifiedTownArrivalDoesNotRestartTheProfile()
        {
            var rig = CreateRig(Profile());
            rig.TeleportSetsCity = false;
            rig.Memory.Hp = 800;
            MarkRunning(rig.Coordinator, flowIndex: 2);
            rig.Coordinator.DeathRecoveryTownTeleportAttempts = 1;

            rig.Coordinator.NotifyPlayerDeathDetected();
            await RunRecoveryAsync(rig.Coordinator);

            Assert.Equal(1, rig.Teleports);
            Assert.Equal(BotPhase.Failed, Phase(rig.Coordinator));
            Assert.Equal(2, FlowIndex(rig.Coordinator));
            Assert.Contains(rig.Snapshot(), line => line.Contains("Town arrival NOT verified"));
            Assert.Contains(rig.Snapshot(), line => line.Contains("the profile is NOT restarted"));
            Assert.DoesNotContain(rig.Snapshot(), line => line.Contains("reset to its first action"));
        }

        [Fact]
        public async Task StillDeadAfterTheWaitNeverTeleportsOrRestarts()
        {
            var rig = CreateRig(Profile());
            rig.Memory.Hp = 0;
            rig.Coordinator.DeathRecoveryWaitMs = 20;
            rig.Coordinator.DeathRecoveryRevivalRecheckWaitMs = 10;
            MarkRunning(rig.Coordinator, flowIndex: 2);

            rig.Coordinator.NotifyPlayerDeathDetected();
            await RunRecoveryAsync(rig.Coordinator);

            Assert.Equal(0, rig.Teleports);
            Assert.Equal(2, FlowIndex(rig.Coordinator));
            Assert.Equal(BotPhase.Failed, Phase(rig.Coordinator));
            Assert.Contains(rig.Snapshot(), line => line.Contains("Player is still dead"));
            Assert.Contains(rig.Snapshot(), line => line.Contains("Revival was not observed"));
            Assert.DoesNotContain(rig.Snapshot(), line => line.Contains("reset to its first action"));
        }

        [Fact]
        public async Task ManualStopDuringTheCountdownPreventsAutomaticRestart()
        {
            var rig = CreateRig(Profile());
            rig.Memory.Hp = 800;
            rig.Coordinator.DeathRecoveryWaitMs = 5000;
            MarkRunning(rig.Coordinator, flowIndex: 2);

            rig.Coordinator.NotifyPlayerDeathDetected();
            var recoveryTask = RunRecoveryAsync(rig.Coordinator);
            await Task.Delay(60);

            // Manual STOP: exactly what MainViewModel.StopWorkflow does.
            rig.Coordinator.Stop();
            await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, rig.Teleports);
            Assert.Equal(2, FlowIndex(rig.Coordinator));
            Assert.Contains(rig.Snapshot(), line => line.Contains("[DeathRecovery] Recovery interrupted"));
            Assert.DoesNotContain(rig.Snapshot(), line => line.Contains("[DeathRecovery] Recovery complete"));
        }

        [Fact]
        public async Task RepeatedDeathNotificationsDoNotRestartTheCountdown()
        {
            var rig = CreateRig(Profile());
            rig.Memory.Hp = 800;
            MarkRunning(rig.Coordinator, flowIndex: 1);

            rig.Coordinator.NotifyPlayerDeathDetected();
            object? firstDue = typeof(BotWorkflowCoordinator)
                .GetField("_deathRecoveryDueUtc", PrivateInstance)!.GetValue(rig.Coordinator);
            DateTime firstDetected = (DateTime)typeof(BotWorkflowCoordinator)
                .GetField("_deathDetectedAtUtc", PrivateInstance)!.GetValue(rig.Coordinator)!;

            await Task.Delay(50);
            rig.Coordinator.NotifyPlayerDeathDetected();

            object? secondDue = typeof(BotWorkflowCoordinator)
                .GetField("_deathRecoveryDueUtc", PrivateInstance)!.GetValue(rig.Coordinator);
            DateTime secondDetected = (DateTime)typeof(BotWorkflowCoordinator)
                .GetField("_deathDetectedAtUtc", PrivateInstance)!.GetValue(rig.Coordinator)!;

            Assert.Equal(firstDue, secondDue);
            Assert.Equal(firstDetected, secondDetected);
            Assert.Equal(1, rig.Count("[DeathRecovery] Death confirmed."));

            await RunRecoveryAsync(rig.Coordinator);
            Assert.Equal(1, rig.Teleports);
        }

        [Fact]
        public async Task SequentialDeathsCreateTwoSeparateRecoveries()
        {
            var rig = CreateRig(Profile());
            rig.Memory.Hp = 800;
            MarkRunning(rig.Coordinator, flowIndex: 2);

            rig.Coordinator.NotifyPlayerDeathDetected();
            await RunRecoveryAsync(rig.Coordinator);
            Assert.Equal(1, rig.Teleports);
            Assert.Equal(0, FlowIndex(rig.Coordinator));

            // The recovered workflow moves on; a second death starts a new, independent episode.
            typeof(BotWorkflowCoordinator).GetField("_flowIndex", PrivateInstance)!
                .SetValue(rig.Coordinator, 2);
            rig.Memory.InCity = false;
            rig.Coordinator.NotifyPlayerDeathDetected();
            await RunRecoveryAsync(rig.Coordinator);

            Assert.Equal(2, rig.Teleports);
            Assert.Equal(2, rig.Count("[DeathRecovery] Death confirmed."));
            Assert.Equal(0, FlowIndex(rig.Coordinator));
        }

        [Fact]
        public void DeathReportWithoutARunningWorkflowDoesNotStartRecovery()
        {
            var rig = CreateRig(Profile());

            rig.Coordinator.NotifyPlayerDeathDetected();

            Assert.False(rig.Coordinator.IsDeathRecoveryRequested);
            Assert.Contains(rig.Snapshot(), line => line.Contains("workflow is not running"));
            Assert.Equal(0, rig.Teleports);
        }
    }
}
