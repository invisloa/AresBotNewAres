using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Ordering and retry coverage for the automatic crash recovery:
    /// kill the old client if needed → wait for internet (five-minute retry, shortened
    /// in these tests) → start/login the client → wait 5 s → attach → wait for the map
    /// → teleport to town → wait for the town map → start the main profile.
    /// </summary>
    public sealed class GameCrashRecoveryTests
    {
        private sealed class Rig
        {
            public readonly List<string> Events = new();
            public readonly HashSet<int> RunningProcesses = new();
            public int InternetChecks;
            public int InternetFailuresBeforeSuccess;
            public bool KillSucceeds = true;
            public bool AttachSucceeds = true;
            public bool MapLoads = true;
            public bool TeleportSucceeds = true;
            public bool StartProfileSucceeds = true;
            public GameClientStartupResult StartupResult =
                new(GameClientStartupStatus.Started, 456, "started");
            public bool? StartupUseMageLogin;
            public BotProfile? TeleportProfile;
            public BotProfile? StartedProfile;

            public GameCrashRecoveryService CreateService()
            {
                var service = new GameCrashRecoveryService(
                    log: line => Events.Add(line),
                    hasInternetConnectionAsync: _ =>
                    {
                        InternetChecks++;
                        Events.Add($"internet:{InternetChecks}");
                        return Task.FromResult(InternetChecks > InternetFailuresBeforeSuccess);
                    },
                    startGameClientAsync: (useMageLogin, _) =>
                    {
                        StartupUseMageLogin = useMageLogin;
                        Events.Add("startup");
                        return Task.FromResult(StartupResult);
                    },
                    attachToGameAsync: (pid, _) =>
                    {
                        Events.Add($"attach:{pid}");
                        return Task.FromResult(AttachSucceeds);
                    },
                    waitForMapLoadedAsync: (_, _) =>
                    {
                        Events.Add("map");
                        return Task.FromResult(MapLoads);
                    },
                    teleportToCityAsync: (profile, _) =>
                    {
                        TeleportProfile = profile;
                        Events.Add("teleport");
                        return Task.FromResult(TeleportSucceeds);
                    },
                    startMainProfileAsync: (profile, _) =>
                    {
                        StartedProfile = profile;
                        Events.Add("start-profile");
                        return Task.FromResult(StartProfileSucceeds);
                    })
                {
                    InternetRetryDelayMs = 1,
                    ProcessStopTimeoutMs = 200,
                    PostCharacterSelectionDelayMs = 1,
                    MapLoadTimeoutMs = 50,
                    PostTeleportMapLoadTimeoutMs = 50
                };

                service.GetGameProcessIds = () => RunningProcesses.ToArray();
                service.IsProcessRunning = pid => RunningProcesses.Contains(pid);
                service.KillProcess = pid =>
                {
                    Events.Add($"kill:{pid}");
                    if (KillSucceeds)
                        RunningProcesses.Remove(pid);
                    return KillSucceeds;
                };
                return service;
            }
        }

        private static BotProfile Profile() => new() { Name = "MainCOT" };

        [Fact]
        public async Task RecoveryKillsOldClientWaitsForInternetAndStartsMainProfileInOrder()
        {
            var rig = new Rig();
            rig.RunningProcesses.Add(123);
            rig.InternetFailuresBeforeSuccess = 1;
            GameCrashRecoveryService service = rig.CreateService();

            GameCrashRecoveryOutcome outcome = await service.RecoverAsync(
                123,
                Profile(),
                useMageLogin: true,
                CancellationToken.None);

            Assert.Equal(GameCrashRecoveryOutcome.Completed, outcome);
            Assert.True(rig.StartupUseMageLogin);
            Assert.Equal("MainCOT", rig.TeleportProfile?.Name);
            Assert.Equal("MainCOT", rig.StartedProfile?.Name);
            Assert.Equal(2, rig.InternetChecks);

            string[] expected = { "kill:123", "internet:1", "internet:2", "startup", "attach:456", "map", "teleport", "map", "start-profile" };
            string[] actual = rig.Events.Where(e => !e.StartsWith("[")).ToArray();
            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task RecoveryDoesNotKillWhenClientProcessAlreadyExited()
        {
            var rig = new Rig();
            GameCrashRecoveryService service = rig.CreateService();

            GameCrashRecoveryOutcome outcome = await service.RecoverAsync(
                123,
                Profile(),
                useMageLogin: false,
                CancellationToken.None);

            Assert.Equal(GameCrashRecoveryOutcome.Completed, outcome);
            Assert.DoesNotContain(rig.Events, e => e.StartsWith("kill:", StringComparison.Ordinal));
            Assert.Contains("attach:456", rig.Events);
        }

        [Fact]
        public async Task RecoveryKeepsCheckingInternetUntilItIsAvailable()
        {
            var rig = new Rig();
            rig.InternetFailuresBeforeSuccess = 3;
            GameCrashRecoveryService service = rig.CreateService();

            GameCrashRecoveryOutcome outcome = await service.RecoverAsync(
                0,
                Profile(),
                useMageLogin: false,
                CancellationToken.None);

            Assert.Equal(GameCrashRecoveryOutcome.Completed, outcome);
            Assert.Equal(4, rig.InternetChecks);
            Assert.Contains(rig.Events, e => e.Contains("No internet connection (check 3)", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RecoveryStopsWhenTheOldClientCannotBeKilled()
        {
            var rig = new Rig();
            rig.RunningProcesses.Add(123);
            rig.KillSucceeds = false;
            GameCrashRecoveryService service = rig.CreateService();
            service.ProcessStopTimeoutMs = 50;

            GameCrashRecoveryOutcome outcome = await service.RecoverAsync(
                123,
                Profile(),
                useMageLogin: false,
                CancellationToken.None);

            Assert.Equal(GameCrashRecoveryOutcome.Failed, outcome);
            Assert.DoesNotContain("internet:1", rig.Events);
            Assert.DoesNotContain("startup", rig.Events);
            Assert.Contains(rig.Events, e => e.Contains("still alive", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RecoveryStopsWhenClientStartupFails()
        {
            var rig = new Rig();
            rig.StartupResult = new GameClientStartupResult(
                GameClientStartupStatus.Failed,
                0,
                "launcher missing");
            GameCrashRecoveryService service = rig.CreateService();

            GameCrashRecoveryOutcome outcome = await service.RecoverAsync(
                0,
                Profile(),
                useMageLogin: false,
                CancellationToken.None);

            Assert.Equal(GameCrashRecoveryOutcome.Failed, outcome);
            Assert.DoesNotContain(rig.Events, e => e.StartsWith("attach:", StringComparison.Ordinal));
            Assert.Contains(rig.Events, e => e.Contains("launcher missing", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RecoveryStopsWhenTheWorldMapNeverLoads()
        {
            var rig = new Rig();
            rig.MapLoads = false;
            GameCrashRecoveryService service = rig.CreateService();

            GameCrashRecoveryOutcome outcome = await service.RecoverAsync(
                0,
                Profile(),
                useMageLogin: false,
                CancellationToken.None);

            Assert.Equal(GameCrashRecoveryOutcome.Failed, outcome);
            Assert.DoesNotContain("teleport", rig.Events);
            Assert.DoesNotContain("start-profile", rig.Events);
        }

        [Fact]
        public async Task TeleportFailureIsLoggedButDoesNotAbortTheMainProfileStart()
        {
            var rig = new Rig();
            rig.TeleportSucceeds = false;
            GameCrashRecoveryService service = rig.CreateService();

            GameCrashRecoveryOutcome outcome = await service.RecoverAsync(
                0,
                Profile(),
                useMageLogin: false,
                CancellationToken.None);

            Assert.Equal(GameCrashRecoveryOutcome.Completed, outcome);
            Assert.Contains("teleport", rig.Events);
            Assert.Contains("start-profile", rig.Events);
            Assert.Contains(rig.Events, e => e.Contains("could not be verified", StringComparison.Ordinal));
        }

        [Fact]
        public async Task CancellingTheInternetWaitStopsTheRecovery()
        {
            var rig = new Rig();
            using var cts = new CancellationTokenSource();
            rig.InternetFailuresBeforeSuccess = int.MaxValue;
            GameCrashRecoveryService service = rig.CreateService();
            service.InternetRetryDelayMs = 10;

            Task<GameCrashRecoveryOutcome> recovery = service.RecoverAsync(
                0,
                Profile(),
                useMageLogin: false,
                cts.Token);
            await Task.Delay(5);
            cts.Cancel();

            GameCrashRecoveryOutcome outcome = await recovery;
            Assert.Equal(GameCrashRecoveryOutcome.Cancelled, outcome);
            Assert.DoesNotContain("startup", rig.Events);
        }
    }
}
