using System;
using System.Collections.Generic;
using System.IO;
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
    /// Focused regression tests for the coordinator side of the map-based startup detection:
    /// startup location validation (including the confirmed stale-COT-coordinate failure),
    /// the flow/route initialization, the one-shot route-entry exception, and the guarantee
    /// that the detection runs only for an explicit <see cref="BotWorkflowCoordinator.StartAsync"/>.
    /// </summary>
    public sealed class StartupWorkflowEntryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly MethodInfo ResolveStartupLocationMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("ResolveStartupLocationAsync", PrivateInstance)
            ?? throw new InvalidOperationException("ResolveStartupLocationAsync not found.");

        private static readonly MethodInfo ApplyStartupSelectionMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("ApplyStartupSelectionAsync", PrivateInstance)
            ?? throw new InvalidOperationException("ApplyStartupSelectionAsync not found.");

        private static readonly MethodInfo ExecuteFlowStepMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("ExecuteFlowStepAsync", PrivateInstance)
            ?? throw new InvalidOperationException("ExecuteFlowStepAsync not found.");

        private static readonly MethodInfo ExecuteExpLoopStepMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("ExecuteExpLoopStepAsync", PrivateInstance)
            ?? throw new InvalidOperationException("ExecuteExpLoopStepAsync not found.");

        private static readonly MethodInfo EnsureRouteEntryValidatedMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("EnsureRouteEntryValidatedAsync", PrivateInstance)
            ?? throw new InvalidOperationException("EnsureRouteEntryValidatedAsync not found.");

        private static readonly MethodInfo RedirectToRepotStepMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("RedirectToRepotStep", PrivateInstance)
            ?? throw new InvalidOperationException("RedirectToRepotStep not found.");

        private static readonly MethodInfo WaitIfPausedMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("WaitIfPausedAsync", PrivateInstance)
            ?? throw new InvalidOperationException("WaitIfPausedAsync not found.");

        private static readonly FieldInfo FlowIndexField = typeof(BotWorkflowCoordinator)
            .GetField("_flowIndex", PrivateInstance)
            ?? throw new InvalidOperationException("_flowIndex not found.");

        private static readonly FieldInfo RepotPathIndexField = typeof(BotWorkflowCoordinator)
            .GetField("_repotPathIndex", PrivateInstance)
            ?? throw new InvalidOperationException("_repotPathIndex not found.");

        private static readonly FieldInfo StepRouteIndexField = typeof(BotWorkflowCoordinator)
            .GetField("_stepRouteIndex", PrivateInstance)
            ?? throw new InvalidOperationException("_stepRouteIndex not found.");

        private static readonly FieldInfo StartupRouteEntryField = typeof(BotWorkflowCoordinator)
            .GetField("_startupRouteEntry", PrivateInstance)
            ?? throw new InvalidOperationException("_startupRouteEntry not found.");

        private static string SaveDirectory => Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));

        // ─────────────────────────── fixtures ───────────────────────────

        /// <summary>
        /// Shipped MainCOT shape: Etana repot (35) → Etana travel (35) → COT entry operation
        /// (no starting map) → COT ExpLoop (36) with the entrance start check enabled.
        /// </summary>
        private static BotProfile CotProfile(string expPathFile) => new()
        {
            Name = "StartupWorkflowCOT",
            EnableStartPositionCheck = true,
            StartPositionX = 214,
            StartPositionY = 213,
            ProtectionMapNumber = 35,
            StartPositionTolerance = 5,
            FlowSteps = new List<BotFlowStep>
            {
                new()
                {
                    Type = BotFlowStepType.Repot,
                    StartMapNumber = 35,
                    RepotPaths = new List<BotRouteStep>
                    {
                        new() { PathFile = "startup-unused-repot.json", StartMapNumber = 35 }
                    }
                },
                new()
                {
                    Type = BotFlowStepType.Path,
                    StartMapNumber = 35,
                    Routes = new List<BotRouteStep>
                    {
                        new() { PathFile = "startup-unused-travel.json", StartMapNumber = 35 }
                    }
                },
                new()
                {
                    Type = BotFlowStepType.Operation,
                    OperationName = "Enter_COT"
                },
                new()
                {
                    Type = BotFlowStepType.ExpLoop,
                    StartMapNumber = 36,
                    Routes = new List<BotRouteStep>
                    {
                        new()
                        {
                            PathFile = expPathFile,
                            StartMapNumber = 36,
                            StartCheckEnabled = true,
                            StartPositionX = 128,
                            StartPositionY = 145,
                            ProtectionMapNumber = 36,
                            StartPositionTolerance = 5
                        }
                    }
                }
            }
        };

        private static BotProfile EtanaProfile() => new()
        {
            Name = "StartupWorkflowEtana",
            EnableStartPositionCheck = true,
            StartPositionX = 214,
            StartPositionY = 213,
            ProtectionMapNumber = 35,
            StartPositionTolerance = 5,
            FlowSteps = new List<BotFlowStep>
            {
                new()
                {
                    Type = BotFlowStepType.Repot,
                    StartMapNumber = 35,
                    RepotPaths = new List<BotRouteStep>
                    {
                        new() { PathFile = "startup-unused-repot.json", StartMapNumber = 35 }
                    }
                },
                new()
                {
                    Type = BotFlowStepType.ExpLoop,
                    StartMapNumber = 36,
                    Routes = new List<BotRouteStep>
                    {
                        new()
                        {
                            PathFile = "startup-unused-exp.json",
                            StartMapNumber = 36,
                            StartCheckEnabled = true,
                            StartPositionX = 128,
                            StartPositionY = 145,
                            ProtectionMapNumber = 36
                        }
                    }
                }
            }
        };

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

        private sealed class Rig
        {
            public List<string> Log { get; init; } = new();
            public FakeMemory Memory { get; init; } = new();
            public BotWorkflowCoordinator Coordinator { get; set; } = null!;
            public PathRunnerService PathRunner { get; set; } = null!;
            public int TownTeleports;
            public int Nudges;
        }

        /// <summary>
        /// Builds the real coordinator stack on top of the simulated player memory. The
        /// position synchronization uses the production service with fast test options and a
        /// deterministic "mini-W" that moves the simulated player.
        /// </summary>
        private static Rig CreateRig(
            BotProfile profile,
            FakeMemory memory,
            Action<FakeMemory, Rig>? onNudge = null)
        {
            var rig = new Rig { Memory = memory };
            // The workflow logs from several tasks; keep the shared log list consistent so
            // assertions can read it while a route is still running.
            Action<string> log = line =>
            {
                lock (rig.Log)
                    rig.Log.Add(line);
            };
            GameMemoryService memoryService = memory.CreateMemory(log);
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
                    rig.Nudges++;
                    onNudge?.Invoke(memory, rig);
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
                    rig.TownTeleports++;
                    return Task.FromResult(true);
                }
            };

            rig.Coordinator = coordinator;
            rig.PathRunner = pathRunner;
            return rig;
        }

        // ─────────────────────── startup location validation ───────────────────────

        [Fact]
        public async Task StalePostPortalCoordinatesAreRefreshedBeforeTheStartupDecision()
        {
            // The confirmed COT failure: the map read already says 36 while X/Y still belong
            // to Etana. The startup resolution must not accept those coordinates.
            var memory = new FakeMemory { Map = 36, X = 335, Y = 146, InCity = false };
            var rig = CreateRig(CotProfile("startup-unused-exp.json"), memory, (m, _) =>
            {
                // The game publishes the real COT spawn once the player moves.
                m.X = 128;
                m.Y = 145;
            });

            PlayerStateSnapshot? resolved = await ResolveStartupLocationAsync(rig.Coordinator);

            Assert.NotNull(resolved);
            Assert.Equal(36, resolved!.Value.Map);
            Assert.Equal(128, resolved.Value.X);
            Assert.Equal(145, resolved.Value.Y);
            Assert.Equal(1, rig.Nudges);
            Assert.Equal(0, rig.TownTeleports);
        }

        [Fact]
        public async Task StaleCoordinatesThatNeverRefreshAbortStartupWithoutTeleport()
        {
            var memory = new FakeMemory { Map = 36, X = 335, Y = 146, InCity = false };
            // The mini-W never changes the published position: the coordinates stay suspect.
            var rig = CreateRig(CotProfile("startup-unused-exp.json"), memory);

            PlayerStateSnapshot? resolved = await ResolveStartupLocationAsync(rig.Coordinator);

            Assert.Null(resolved);
            Assert.Equal(0, rig.TownTeleports);
            Assert.Contains(rig.Log, line => line.Contains("[Startup] Position NOT validated"));
            Assert.Contains(rig.Log, line => line.Contains("no waypoint will be selected and no teleport is attempted"));
        }

        [Theory]
        [InlineData("no-pointer")]
        [InlineData("no-map")]
        [InlineData("zero-map")]
        [InlineData("no-position")]
        public async Task AnUntrustworthyLocationPreventsTheStartupDecision(string failure)
        {
            var memory = new FakeMemory { Map = 36, X = 128, Y = 145, InCity = false };
            switch (failure)
            {
                case "no-pointer": memory.FailPointer = true; break;
                case "no-map": memory.FailMap = true; break;
                case "zero-map": memory.Map = 0; break;
                case "no-position": memory.FailPosition = true; break;
            }

            var rig = CreateRig(CotProfile("startup-unused-exp.json"), memory, (m, _) => { m.X = 129; });

            PlayerStateSnapshot? resolved = await ResolveStartupLocationAsync(rig.Coordinator);

            Assert.Null(resolved);
            Assert.Equal(0, rig.TownTeleports);
            Assert.Contains(rig.Log, line => line.Contains("[Startup] Player location is not readable"));
        }

        // ─────────────────────── flow / route initialization ───────────────────────

        [Fact]
        public async Task StartingOnCotSelectsTheExpLoopAndDoesNotUseTheEtanaStartPosition()
        {
            var memory = new FakeMemory { Map = 36, X = 100, Y = 100, InCity = false };
            var rig = CreateRig(CotProfile("startup-unused-exp.json"), memory, (m, _) => { m.X = 101; });

            string outcome = await ApplyStartupSelectionAsync(rig.Coordinator);

            Assert.Equal("Selected", outcome);
            Assert.Equal(3, GetFlowIndex(rig.Coordinator));
            Assert.Equal(0, GetStepRouteIndex(rig.Coordinator, rig.Coordinator.ActiveProfile.FlowSteps[3]));
            Assert.NotNull(StartupRouteEntryField.GetValue(rig.Coordinator));
            Assert.Contains("start map 36", rig.Coordinator.StartupSelectionText);
            Assert.Contains(rig.Log, line => line.Contains("[Startup] Selected"));
            Assert.Contains(rig.Log, line => line.Contains("Earlier flow steps are not executed on this start"));
            Assert.Equal(0, rig.TownTeleports);
            Assert.Contains(rig.Log, line => line.Contains("[Startup] Position validated"));
        }

        [Fact]
        public async Task StartingOnEtanaSelectsTheRepotActionWithoutMoving()
        {
            var memory = new FakeMemory { Map = 35, X = 214, Y = 213, InCity = true };
            var rig = CreateRig(EtanaProfile(), memory);

            string outcome = await ApplyStartupSelectionAsync(rig.Coordinator);

            Assert.Equal("Selected", outcome);
            Assert.Equal(0, GetFlowIndex(rig.Coordinator));
            Assert.Equal(0, GetRepotPathIndex(rig.Coordinator));
            // The Repot step uses its own in-city validation instead of the one-shot route entry.
            Assert.Null(StartupRouteEntryField.GetValue(rig.Coordinator));
            Assert.Contains("start map 35", rig.Coordinator.StartupSelectionText);
            // The player already stands on the profile's declared start position.
            Assert.Equal(0, rig.Nudges);
            Assert.Equal(0, rig.TownTeleports);
        }

        [Fact]
        public async Task StartingOnAnUnsupportedMapFailsClearlyWithoutFallbackOrTeleport()
        {
            var memory = new FakeMemory { Map = 77, X = 10, Y = 10, InCity = false };
            var rig = CreateRig(CotProfile("startup-unused-exp.json"), memory, (m, _) => { m.X = 11; });

            string outcome = await ApplyStartupSelectionAsync(rig.Coordinator);

            Assert.Equal("Failed", outcome);
            Assert.Equal(0, GetFlowIndex(rig.Coordinator));
            Assert.Null(StartupRouteEntryField.GetValue(rig.Coordinator));
            Assert.Equal(string.Empty, rig.Coordinator.StartupSelectionText);
            Assert.Equal(0, rig.TownTeleports);
            Assert.Contains(rig.Log, line => line.Contains("observed map 77") && line.Contains("does not match any configured starting action"));
            Assert.Contains(rig.Log, line => line.Contains("No unrelated action was chosen and no teleport was attempted"));
        }

        [Fact]
        public async Task AProfileWithoutStartingMapsFailsClearlyInsteadOfStartingAtStepOne()
        {
            var profile = new BotProfile
            {
                Name = "StartupWorkflowNoStartMaps",
                FlowSteps = new List<BotFlowStep>
                {
                    new() { Type = BotFlowStepType.Operation, OperationName = "Enter_COT" },
                    new() { Type = BotFlowStepType.ExpLoop, PathFile = "startup-unused-exp.json" }
                }
            };
            var memory = new FakeMemory { Map = 36, X = 100, Y = 100 };
            var rig = CreateRig(profile, memory);

            string outcome = await ApplyStartupSelectionAsync(rig.Coordinator);

            Assert.Equal("Failed", outcome);
            Assert.Equal(0, GetFlowIndex(rig.Coordinator));
            Assert.Null(StartupRouteEntryField.GetValue(rig.Coordinator));
            Assert.Equal(string.Empty, rig.Coordinator.StartupSelectionText);
            Assert.Contains(rig.Log, line => line.Contains("No flow step or route declares a starting map"));
            Assert.Contains(rig.Log, line => line.Contains("the workflow will not start"));
        }

        // ─────────────────────── route entry gates / resumption ───────────────────────

        [Fact]
        public async Task CotStartupEntryBypassesTheEntranceCoordinatesAndReusesRouteResync()
        {
            string pathFile = CreateCsvPathFile();
            try
            {
                var memory = new FakeMemory { Map = 36, X = 100, Y = 100, InCity = false };
                var profile = CotProfile(pathFile);
                var rig = CreateRig(profile, memory, (m, _) =>
                {
                    // A normal mini-W step: the player moves one tile, never to the entrance.
                    m.X = (short)(m.X + 1);
                });

                string outcome = await ApplyStartupSelectionAsync(rig.Coordinator);
                Assert.Equal("Selected", outcome);

                BotFlowStep expStep = profile.FlowSteps[3];
                using var cts = new CancellationTokenSource();
                var task = (Task<bool>?)ExecuteExpLoopStepMethod.Invoke(
                    rig.Coordinator,
                    new object[] { expStep, cts.Token });
                Assert.NotNull(task);

                // Wait until the existing MovementSystem initial resync has selected a waypoint,
                // then stop the hunting loop again (it would otherwise loop forever).
                await WaitForLogAsync(rig.Log, "[RouteResync] bestSegment=", TimeSpan.FromSeconds(15));
                cts.Cancel();
                await task!;

                Assert.Contains(rig.Log, line => line.Contains("[Startup] ExpLoop route") && line.Contains("entry validated on map 36"));
                Assert.DoesNotContain(rig.Log, line => line.Contains("has the start check enabled"));
                Assert.Equal(0, rig.TownTeleports);
                Assert.Null(StartupRouteEntryField.GetValue(rig.Coordinator));
                Assert.Contains(rig.Log, line => line.Contains("[Startup] The one-shot startup route entry"));
                Assert.Contains(rig.Log, line => line.Contains("is consumed"));

                // The player started in the middle of the route: the existing nearest-segment
                // resync must pick a forward waypoint instead of restarting at index 0/1.
                string resyncLine;
                lock (rig.Log)
                    resyncLine = rig.Log.First(line => line.Contains("[RouteResync]") && line.Contains("nextIndex="));
                int nextIndex = ParseNextIndex(resyncLine);
                Assert.True(nextIndex > 1, $"Expected a middle-of-route resume, but got nextIndex={nextIndex}: {resyncLine}");

                // Normal route protection must be back for every later entry.
                var route = expStep.Routes[0];
                bool normalEntryValidated = await EnsureRouteEntryValidatedAsync(rig.Coordinator, route);
                Assert.False(normalEntryValidated);
                Assert.Contains(rig.Log, line => line.Contains("Route entry position NOT validated"));
            }
            finally
            {
                try { File.Delete(Path.Combine(SaveDirectory, pathFile)); } catch { }
            }
        }

        [Fact]
        public async Task RouteRotationContinuesFromTheInitiallySelectedAlternativeRoute()
        {
            var profile = new BotProfile
            {
                Name = "StartupWorkflowRouteRotation",
                EnableStartPositionCheck = true,
                StartPositionX = 214,
                StartPositionY = 213,
                ProtectionMapNumber = 35,
                StartPositionTolerance = 5,
                FlowSteps = new List<BotFlowStep>
                {
                    new()
                    {
                        Type = BotFlowStepType.Repot,
                        StartMapNumber = 35,
                        RepotPaths = new List<BotRouteStep>
                        {
                            new() { PathFile = "startup-unused-repot.json", StartMapNumber = 35 }
                        }
                    },
                    new()
                    {
                        Type = BotFlowStepType.ExpLoop,
                        StartMapNumber = 36,
                        Routes = new List<BotRouteStep>
                        {
                            // The first route belongs to another map, so the SECOND route is the
                            // first match and must become the rotation start.
                            new() { PathFile = "startup-unused-exp-a.json", StartMapNumber = 35 },
                            new() { PathFile = "startup-unused-exp-b.json", StartMapNumber = 36 },
                            new() { PathFile = "startup-unused-exp-c.json", StartMapNumber = 36 }
                        }
                    }
                }
            };

            var memory = new FakeMemory { Map = 36, X = 100, Y = 100, InCity = false };
            var rig = CreateRig(profile, memory, (m, _) => { m.X = (short)(m.X + 1); });

            Assert.Equal("Selected", await ApplyStartupSelectionAsync(rig.Coordinator));

            BotFlowStep expStep = profile.FlowSteps[1];
            Assert.Equal(1, GetFlowIndex(rig.Coordinator));
            Assert.Equal(1, GetStepRouteIndex(rig.Coordinator, expStep));
            Assert.NotNull(StartupRouteEntryField.GetValue(rig.Coordinator));

            // The existing rotation must continue from the selected route (2nd -> 3rd -> 1st).
            var pool = BotStartupActionSelector.GetRoutePool(expStep);
            Assert.NotNull(pool);
            InvokeAdvanceRoute(rig.Coordinator, expStep, pool!);
            Assert.Equal(2, GetStepRouteIndex(rig.Coordinator, expStep));
            InvokeAdvanceRoute(rig.Coordinator, expStep, pool!);
            Assert.Equal(0, GetStepRouteIndex(rig.Coordinator, expStep));
            Assert.Equal(1, CountStartupSelections(rig.Log));
        }

        // ─────────────────────── one-shot lifecycle ───────────────────────

        [Fact]
        public async Task RuntimeTransitionsPauseAndForeignStepsNeverRerunTheStartupDetection()
        {
            string pathFile = CreateCsvPathFile();
            try
            {
                var memory = new FakeMemory { Map = 36, X = 100, Y = 100, InCity = false };
                var profile = CotProfile(pathFile);
                var rig = CreateRig(profile, memory, (m, _) => { m.X = (short)(m.X + 1); });

                Assert.Equal("Selected", await ApplyStartupSelectionAsync(rig.Coordinator));
                int startupSelections = CountStartupSelections(rig.Log);
                Assert.Equal(1, startupSelections);

                // A runtime redirect (automatic repot / recovery) only moves the flow position.
                Assert.True((bool)(RedirectToRepotStepMethod.Invoke(rig.Coordinator, new object[] { "test redirect" }) ?? false));
                Assert.Equal(0, GetFlowIndex(rig.Coordinator));
                Assert.Equal(startupSelections, CountStartupSelections(rig.Log));

                // Pause/resume keeps the same action and route index and never re-detects.
                var pause = new BotPauseController();
                rig.Coordinator.PauseController = pause;
                pause.Pause();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var pausedTask = (Task?)WaitIfPausedMethod.Invoke(rig.Coordinator, new object[] { cts.Token });
                Assert.NotNull(pausedTask);
                await Task.Delay(100);
                Assert.Equal(0, GetFlowIndex(rig.Coordinator));
                pause.Resume();
                await pausedTask!;
                Assert.Equal(startupSelections, CountStartupSelections(rig.Log));

                // Executing a DIFFERENT step discards the one-shot startup entry instead of
                // letting it bypass protection for an unrelated action.
                BotFlowStep repotStep = profile.FlowSteps[0];
                Assert.NotNull(StartupRouteEntryField.GetValue(rig.Coordinator));
                var executeForeignTask = (Task<bool>?)ExecuteFlowStepMethod.Invoke(
                    rig.Coordinator,
                    new object[] { repotStep, CancellationToken.None });
                // The Repot step cannot run here (no camera session / not in city), but the
                // startup entry must already be gone and the detection must not have rerun.
                if (executeForeignTask != null)
                {
                    try { await executeForeignTask; } catch (Exception) { }
                }

                Assert.Null(StartupRouteEntryField.GetValue(rig.Coordinator));
                Assert.Contains(rig.Log, line => line.Contains("was discarded because the workflow position moved to a different step"));
                Assert.Equal(startupSelections, CountStartupSelections(rig.Log));
            }
            finally
            {
                try { File.Delete(Path.Combine(SaveDirectory, pathFile)); } catch { }
            }
        }

        [Fact]
        public async Task RepeatedStartDuringAnActiveRunKeepsTheExistingRuntime()
        {
            string pathFile = CreateCsvPathFile();
            try
            {
                var memory = new FakeMemory { Map = 36, X = 100, Y = 100, InCity = false };
                var profile = new BotProfile
                {
                    Name = "StartupWorkflowRepeatedStart",
                    EnableStartPositionCheck = true,
                    StartPositionX = 214,
                    StartPositionY = 213,
                    ProtectionMapNumber = 35,
                    StartPositionTolerance = 5,
                    FlowSteps = new List<BotFlowStep>
                    {
                        new()
                        {
                            Type = BotFlowStepType.ExpLoop,
                            StartMapNumber = 36,
                            Routes = new List<BotRouteStep>
                            {
                                new() { PathFile = pathFile, StartMapNumber = 36, ProtectionMapNumber = 36 }
                            }
                        }
                    }
                };
                var rig = CreateRig(profile, memory, (m, _) => { m.X = (short)(m.X + 1); });

                Task runTask = rig.Coordinator.StartAsync();
                await WaitForLogAsync(rig.Log, "[ExpLoop] Loading hunting path", TimeSpan.FromSeconds(15));
                Assert.True(rig.Coordinator.IsRunning);

                // A second explicit start while the runtime is active must not replace it.
                await rig.Coordinator.StartAsync();
                Assert.Contains(rig.Log, line => line.Contains("Already running"));
                Assert.Equal(1, CountStartupSelections(rig.Log));

                rig.Coordinator.Stop();
                await WaitForTaskAsync(runTask, TimeSpan.FromSeconds(15));
                Assert.False(rig.Coordinator.IsRunning);
                Assert.Equal(1, CountStartupSelections(rig.Log));
            }
            finally
            {
                try { File.Delete(Path.Combine(SaveDirectory, pathFile)); } catch { }
            }
        }

        [Fact]
        public async Task AStartupFailurePreventsTheWorkflowFromEnteringTheLoop()
        {
            // The stale post-portal coordinates never refresh, so the startup resolution must
            // fail and the workflow must never enter the loop, navigate or teleport.
            var memory = new FakeMemory { Map = 36, X = 335, Y = 146, InCity = false };
            var rig = CreateRig(CotProfile("startup-unused-exp.json"), memory);

            await WaitForTaskAsync(rig.Coordinator.StartAsync(), TimeSpan.FromSeconds(20));

            Assert.False(rig.Coordinator.IsRunning);
            Assert.Equal(BotPhase.Failed, rig.Coordinator.CurrentPhase);
            Assert.Equal(string.Empty, rig.Coordinator.StartupSelectionText);
            Assert.Null(StartupRouteEntryField.GetValue(rig.Coordinator));
            Assert.Equal(0, rig.TownTeleports);
            Assert.Contains(rig.Log, line => line.Contains("[Startup] The bot will not start"));
            Assert.DoesNotContain(rig.Log, line => line.Contains("[Startup] Selected"));
            Assert.DoesNotContain(rig.Log, line => line.Contains("[PathRunner] Started path"));
            Assert.DoesNotContain(rig.Log, line => line.Contains("[ExpLoop] Loading hunting path"));
        }

        [Fact]
        public async Task AutomaticRepotAfterExpRotatesNormallyAndDoesNotRerunStartupDetection()
        {
            string repotPathFile = CreateArrivalPathFile();
            try
            {
                var memory = new FakeMemory { Map = 36, X = 100, Y = 100, InCity = false };
                var profile = CotProfile("startup-unused-exp.json");
                // Two repot routes: the first is selected/consumed here, the next repot must
                // use the existing rotation.
                profile.DryRunRepot = true;
                profile.FlowSteps[0].RepotPaths = new List<BotRouteStep>
                {
                    new() { PathFile = repotPathFile, StartMapNumber = 35 },
                    new() { PathFile = repotPathFile, StartMapNumber = 35 }
                };
                var rig = CreateRig(profile, memory, (m, _) => { m.X = (short)(m.X + 1); });

                Assert.Equal("Selected", await ApplyStartupSelectionAsync(rig.Coordinator));
                int startupSelections = CountStartupSelections(rig.Log);

                // The EXP loop asked for a repot: the existing normal transition applies.
                Assert.True((bool)(RedirectToRepotStepMethod.Invoke(
                    rig.Coordinator,
                    new object[] { "ExpLoop repot condition" }) ?? false));
                Assert.Equal(0, GetFlowIndex(rig.Coordinator));

                // The repot path ends where the player already stands, so the existing route
                // runner completes it immediately (DryRunRepot skips the shop sequence).
                memory.Map = 35;
                memory.InCity = true;
                memory.AutoWalk = true;
                memory.WalkTargetX = 100;
                memory.WalkTargetY = 101;
                var repotTask = (Task<bool>?)ExecuteFlowStepMethod.Invoke(
                    rig.Coordinator,
                    new object[] { profile.FlowSteps[0], CancellationToken.None });
                Assert.NotNull(repotTask);
                bool advanced = await repotTask!;

                if (!advanced)
                {
                    string tail;
                    lock (rig.Log)
                        tail = string.Join(Environment.NewLine, rig.Log.TakeLast(45));
                    Assert.Fail("The repot step must complete through the normal route runner." + Environment.NewLine + tail);
                }
                Assert.Equal(1, GetRepotPathIndex(rig.Coordinator));
                Assert.Equal(startupSelections, CountStartupSelections(rig.Log));
                Assert.Contains(rig.Log, line => line.Contains("[Repot] Repot completed") || line.Contains("DryRunRepot=true"));
            }
            finally
            {
                try { File.Delete(Path.Combine(SaveDirectory, repotPathFile)); } catch { }
            }
        }

        [Fact]
        public async Task StopThenExplicitStartRunsTheStartupDetectionAgainWithTheNewPosition()
        {
            string pathFile = CreateCsvPathFile();
            string repotPathFile = CreateArrivalPathFile();
            try
            {
                var memory = new FakeMemory { Map = 36, X = 100, Y = 100, InCity = false };
                var profile = CotProfile(pathFile);
                profile.DryRunRepot = true;
                profile.FlowSteps[0].RepotPaths = new List<BotRouteStep>
                {
                    new() { PathFile = repotPathFile, StartMapNumber = 35 }
                };
                var rig = CreateRig(profile, memory, (m, _) => { m.X = (short)(m.X + 1); });

                Task firstRun = rig.Coordinator.StartAsync();
                await WaitForLogCountAsync(rig.Log, "[Startup] Selected", 1, TimeSpan.FromSeconds(20));
                rig.Coordinator.Stop();
                await WaitForTaskAsync(firstRun, TimeSpan.FromSeconds(20));
                Assert.False(rig.Coordinator.IsRunning);
                Assert.Contains("start map 36", rig.Coordinator.StartupSelectionText);

                // The player moved to Etana while the bot was stopped; a new explicit start
                // must run the detection again against the newly observed position.
                memory.Map = 35;
                memory.X = 214;
                memory.Y = 213;
                memory.InCity = true;

                Task secondRun = rig.Coordinator.StartAsync();
                await WaitForLogCountAsync(rig.Log, "[Startup] Selected", 2, TimeSpan.FromSeconds(20));
                rig.Coordinator.Stop();
                await WaitForTaskAsync(secondRun, TimeSpan.FromSeconds(20));

                Assert.Equal(2, CountStartupSelections(rig.Log));
                Assert.Contains("start map 35", rig.Coordinator.StartupSelectionText);
                Assert.Equal(0, GetFlowIndex(rig.Coordinator));
            }
            finally
            {
                try { File.Delete(Path.Combine(SaveDirectory, pathFile)); } catch { }
                try { File.Delete(Path.Combine(SaveDirectory, repotPathFile)); } catch { }
            }
        }

        [Fact]
        public async Task StopDuringStartupAbortsWithoutNavigationOrTeleport()
        {
            var memory = new FakeMemory { Map = 36, X = 335, Y = 146, InCity = false };
            var rig = CreateRig(CotProfile("startup-unused-exp.json"), memory);

            // The user stops the bot while the startup location is being validated.
            rig.Coordinator.Stop();

            await WaitForTaskAsync(rig.Coordinator.StartAsync(), TimeSpan.FromSeconds(15));

            Assert.False(rig.Coordinator.IsRunning);
            Assert.Equal(string.Empty, rig.Coordinator.StartupSelectionText);
            Assert.Null(StartupRouteEntryField.GetValue(rig.Coordinator));
            Assert.Equal(0, rig.TownTeleports);
            Assert.DoesNotContain(rig.Log, line => line.Contains("[ExpLoop] Loading hunting path"));
            Assert.DoesNotContain(rig.Log, line => line.Contains("[PathRunner] Started path"));
        }

        // ─────────────────────── helpers ───────────────────────

        private static async Task<PlayerStateSnapshot?> ResolveStartupLocationAsync(BotWorkflowCoordinator coordinator)
        {
            object? raw = ResolveStartupLocationMethod.Invoke(coordinator, new object[] { CancellationToken.None });
            Assert.NotNull(raw);
            var task = (Task)raw!;
            await task;
            object? result = task.GetType().GetProperty("Result")?.GetValue(task);
            return (PlayerStateSnapshot?)result;
        }

        private static async Task<string> ApplyStartupSelectionAsync(BotWorkflowCoordinator coordinator)
        {
            object? raw = ApplyStartupSelectionMethod.Invoke(coordinator, new object[] { CancellationToken.None });
            Assert.NotNull(raw);
            var task = (Task)raw!;
            await task;
            object? result = task.GetType().GetProperty("Result")?.GetValue(task);
            return result?.ToString() ?? "null";
        }

        private static async Task<bool> EnsureRouteEntryValidatedAsync(
            BotWorkflowCoordinator coordinator,
            BotRouteStep route)
        {
            var task = (Task<bool>?)EnsureRouteEntryValidatedMethod.Invoke(
                coordinator,
                new object[] { route, "ExpLoop", CancellationToken.None });
            Assert.NotNull(task);
            return await task!;
        }

        private static void InvokeAdvanceRoute(
            BotWorkflowCoordinator coordinator,
            BotFlowStep step,
            List<BotRouteStep> pool)
        {
            MethodInfo method = typeof(BotWorkflowCoordinator).GetMethod("AdvanceRoute", PrivateInstance)
                ?? throw new InvalidOperationException("AdvanceRoute not found.");
            method.Invoke(coordinator, new object[] { step, pool });
        }

        private static int GetFlowIndex(BotWorkflowCoordinator coordinator)
            => (int)(FlowIndexField.GetValue(coordinator) ?? -1);

        private static int GetRepotPathIndex(BotWorkflowCoordinator coordinator)
            => (int)(RepotPathIndexField.GetValue(coordinator) ?? -1);

        private static int GetStepRouteIndex(BotWorkflowCoordinator coordinator, BotFlowStep step)
        {
            var map = (Dictionary<BotFlowStep, int>?)StepRouteIndexField.GetValue(coordinator);
            return map != null && map.TryGetValue(step, out int index) ? index : 0;
        }

        private static int CountStartupSelections(List<string> log)
        {
            lock (log)
                return log.Count(line => line.Contains("[Startup] Selected"));
        }

        private static int ParseNextIndex(string resyncLine)
        {
            int marker = resyncLine.IndexOf("nextIndex=", StringComparison.Ordinal);
            Assert.True(marker >= 0, $"No nextIndex in: {resyncLine}");
            string tail = resyncLine.Substring(marker + "nextIndex=".Length);
            int end = 0;
            while (end < tail.Length && char.IsDigit(tail[end]))
                end++;
            Assert.True(end > 0, $"No digits after nextIndex= in: {resyncLine}");
            return int.Parse(tail.Substring(0, end));
        }

        private static async Task WaitForLogAsync(List<string> log, string fragment, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (log)
                {
                    if (log.Any(line => line.Contains(fragment)))
                        return;
                }
                await Task.Delay(25);
            }

            string snapshot;
            lock (log)
                snapshot = string.Join(Environment.NewLine, log.TakeLast(40));
            throw new Xunit.Sdk.XunitException(
                $"Timed out waiting for log '{fragment}'. Last lines:{Environment.NewLine}{snapshot}");
        }

        private static async Task WaitForTaskAsync(Task task, TimeSpan timeout)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeout));
            Assert.Same(task, completed);
            await task;
        }

        /// <summary>
        /// Creates a 2-point path that already ends on the simulated player position (100,100),
        /// so the existing route runner completes it without any simulated walking.
        /// </summary>
        private static string CreateArrivalPathFile()
        {
            Directory.CreateDirectory(SaveDirectory);
            string fileName = $"StartupEntryArrival_{Guid.NewGuid():N}.json";
            File.WriteAllText(
                Path.Combine(SaveDirectory, fileName),
                "{\"Name\":\"startup-entry-arrival\",\"Points\":[" +
                "{\"X\":100,\"Y\":100,\"Precision\":2,\"Mode\":0,\"ZoneRestriction\":1}," +
                "{\"X\":100,\"Y\":101,\"Precision\":2,\"Mode\":0,\"ZoneRestriction\":1}]}");
            return fileName;
        }

        private static async Task WaitForLogCountAsync(
            List<string> log,
            string fragment,
            int expectedCount,
            TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                int count;
                lock (log)
                    count = log.Count(line => line.Contains(fragment));
                if (count >= expectedCount)
                    return;
                await Task.Delay(25);
            }

            string snapshot;
            lock (log)
                snapshot = string.Join(Environment.NewLine, log.TakeLast(40));
            throw new Xunit.Sdk.XunitException(
                $"Timed out waiting for {expectedCount}x '{fragment}'. Last lines:{Environment.NewLine}{snapshot}");
        }

        /// <summary>
        /// Creates a 6-point hunting path in the test SavedPaths so the initial route resync
        /// can pick a waypoint in the MIDDLE of the route for a player at (100,100).
        /// </summary>
        private static string CreateCsvPathFile()
        {
            Directory.CreateDirectory(SaveDirectory);
            string fileName = $"StartupEntryRoute_{Guid.NewGuid():N}.json";
            File.WriteAllText(
                Path.Combine(SaveDirectory, fileName),
                "{\"Name\":\"startup-entry-route\",\"LoopRoute\":true,\"Points\":[" +
                "{\"X\":40,\"Y\":40,\"Precision\":2,\"Mode\":0}," +
                "{\"X\":60,\"Y\":60,\"Precision\":2,\"Mode\":0}," +
                "{\"X\":80,\"Y\":80,\"Precision\":2,\"Mode\":0}," +
                "{\"X\":100,\"Y\":100,\"Precision\":2,\"Mode\":0}," +
                "{\"X\":120,\"Y\":120,\"Precision\":2,\"Mode\":0}," +
                "{\"X\":140,\"Y\":140,\"Precision\":2,\"Mode\":0}]}");
            return fileName;
        }

        /// <summary>Minimal player-memory simulation (player pointer + map + X/Y + city flag).</summary>
        private sealed class FakeMemory
        {
            public const ulong ModuleBase = 0x100000;
            public const ulong PlayerBase = 0x200000;
            public const ulong CameraBase = 0x300000;

            public int Map = 35;
            public short X = 100;
            public short Y = 100;
            public bool InCity;
            public short VerticalLock = BotConstants.Camera.StationaryVerticalLock;
            public bool FailPointer;
            public bool FailMap;
            public bool FailPosition;

            /// <summary>
            /// When enabled, every position read advances the simulated player one tile towards
            /// (WalkTargetX, WalkTargetY) — a deterministic stand-in for the game's own movement,
            /// so non-loop routes can reach their final waypoint in tests.
            /// </summary>
            public bool AutoWalk;
            public short WalkTargetX = 100;
            public short WalkTargetY = 101;

            private short ReadX()
            {
                AdvanceWalk();
                return X;
            }

            private short ReadY()
            {
                AdvanceWalk();
                return Y;
            }

            private void AdvanceWalk()
            {
                if (!AutoWalk)
                    return;
                if (X != WalkTargetX)
                    X = (short)(X + Math.Sign(WalkTargetX - X));
                if (Y != WalkTargetY)
                    Y = (short)(Y + Math.Sign(WalkTargetY - Y));
            }

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
                        BotConstants.MemoryOffsets.CurrentMap when !FailMap =>
                            Copy(buffer, BitConverter.GetBytes(Map), out bytesRead),
                        BotConstants.MemoryOffsets.X when !FailPosition =>
                            Copy(buffer, BitConverter.GetBytes(ReadX()), out bytesRead),
                        BotConstants.MemoryOffsets.Y when !FailPosition =>
                            Copy(buffer, BitConverter.GetBytes(ReadY()), out bytesRead),
                        BotConstants.MemoryOffsets.IsInCity =>
                            Copy(buffer, BitConverter.GetBytes(InCity ? 1 : 0), out bytesRead),
                        _ => false
                    };
                }

                return false;
            }

            private bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = 0;
                if (address == CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset && buffer.Length >= 2)
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
    }
}
