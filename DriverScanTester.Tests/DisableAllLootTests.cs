using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the profile/path "Disable All Loot" policy. The policy
    /// suppresses every automatic loot operation (normal scans, SOD/SOP scans, passive
    /// pre-scans, loot camera/screenshots, collection and mandatory barriers) while
    /// movement, combat retargeting, mob grouping and player-marker safety keep working.
    /// </summary>
    public sealed class DisableAllLootTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static string SaveDirectory => Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));

        private static string ProfileDirectory => Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedBotProfiles"));

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

        private static LootSystem CreateLoot(Action<string> log)
            => new LootSystem(CreateStubMemory(log), log);

        private static MovementSystem CreateMovement(
            Action<string> log,
            BotMode mode = BotMode.MoveAndAttackAndLoot,
            bool disableAllLoot = false,
            bool profileOverride = false,
            LootSystem? loot = null)
        {
            var path = new List<Waypoint>
            {
                new(1000, 1000, MovementPrecision.Medium, mode, disableAllLoot: disableAllLoot)
            };
            var movement = new MovementSystem(
                CreateStubMemory(log),
                log,
                1000,
                1000,
                customPath: path)
            {
                InternalRepotEnabled = false,
                LootSystemRef = loot,
                DisableAllLootOverride = profileOverride
            };
            return movement;
        }

        private static void InvokePrivate(object target, string method, params object?[] args)
        {
            MethodInfo info = target.GetType().GetMethod(method, PrivateInstance)
                ?? throw new InvalidOperationException($"Method {method} was not found on {target.GetType().Name}.");
            info.Invoke(target, args);
        }

        private static object? InvokePrivateResult(object target, string method, params object?[] args)
        {
            MethodInfo info = target.GetType().GetMethod(method, PrivateInstance)
                ?? throw new InvalidOperationException($"Method {method} was not found on {target.GetType().Name}.");
            return info.Invoke(target, args);
        }

        private static bool BarrierHoldsInput(MovementSystem movement)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_lootNavigationBarrierHoldsInput", PrivateInstance)
                ?? throw new InvalidOperationException("barrier input field was not found.");
            return (bool)field.GetValue(movement)!;
        }

        private static object? GetPrivateField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, PrivateInstance)
                ?? throw new InvalidOperationException($"Field {name} was not found.");
            return field.GetValue(target);
        }

        private static void SetPrivateField(object target, string name, object? value)
        {
            FieldInfo field = target.GetType().GetField(name, PrivateInstance)
                ?? throw new InvalidOperationException($"Field {name} was not found.");
            field.SetValue(target, value);
        }

        private static async Task<bool> InvokeAdvanceAsync(MovementSystem movement, float x, float y)
        {
            MethodInfo info = typeof(MovementSystem).GetMethod("AdvanceReachedWaypoints", PrivateInstance)
                ?? throw new InvalidOperationException("AdvanceReachedWaypoints was not found.");
            var task = (Task<bool>?)info.Invoke(movement, new object[] { x, y, CancellationToken.None });
            Assert.NotNull(task);
            return await task!;
        }

        // ─────────────────────────── persistence ───────────────────────────

        [Fact]
        public void NewProfileDefaultsToAutomaticLootEnabled()
        {
            Assert.False(new BotProfile().DisableAllLoot);
        }

        [Fact]
        public void ExistingProfileJsonWithoutTheFieldLoadsAsFalse()
        {
            BotProfile? profile = JsonSerializer.Deserialize<BotProfile>(
                "{\"Name\":\"legacy\",\"FlowSteps\":[{\"Type\":1,\"PathFile\":\"old.json\"}]}");

            Assert.NotNull(profile);
            Assert.False(profile!.DisableAllLoot);
        }

        [Fact]
        public void ProfileDisableAllLootSurvivesTheLoaderSaveLoadRoundTrip()
        {
            Directory.CreateDirectory(ProfileDirectory);
            string name = $"DisableAllLoot_{Guid.NewGuid():N}";
            string path = Path.Combine(ProfileDirectory, name + ".json");
            var log = new List<string>();

            try
            {
                var loader = new BotProfileLoader(log.Add);
                var profile = new BotProfile
                {
                    Name = name,
                    DisableAllLoot = true,
                    FlowSteps = new List<BotFlowStep>
                    {
                        new() { Type = BotFlowStepType.Operation, OperationName = "GoInsideCOT" }
                    }
                };

                Assert.True(loader.SaveProfile(profile));
                BotProfile? loaded = loader.LoadProfile(name);

                Assert.NotNull(loaded);
                Assert.True(loaded!.DisableAllLoot);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void NewPathSegmentDefaultsToAutomaticLootEnabled()
        {
            Assert.False(new PathSegment().DisableAllLoot);
        }

        [Fact]
        public void ExistingPathJsonWithoutTheFieldLoadsAsFalse()
        {
            PathSegment? segment = JsonSerializer.Deserialize<PathSegment>(
                "{\"Name\":\"legacy\",\"Points\":[{\"X\":1,\"Y\":2}]}");

            Assert.NotNull(segment);
            Assert.False(segment!.DisableAllLoot);
        }

        [Fact]
        public void PathDisableAllLootSurvivesAJsonRoundTrip()
        {
            var segment = new PathSegment
            {
                Name = "policy",
                DisableAllLoot = true,
                Points = new List<PathPoint>
                {
                    new(10, 20),
                    new(30, 40)
                }
            };

            string json = JsonSerializer.Serialize(segment);
            PathSegment? loaded = JsonSerializer.Deserialize<PathSegment>(json);

            Assert.NotNull(loaded);
            Assert.True(loaded!.DisableAllLoot);
        }

        [Fact]
        public void SavedPathLoaderPropagatesThePathPolicyToEveryRuntimeWaypoint()
        {
            Directory.CreateDirectory(SaveDirectory);
            string fileName = $"DisableAllLoot_{Guid.NewGuid():N}.json";
            string fullPath = Path.Combine(SaveDirectory, fileName);

            try
            {
                var segment = new PathSegment
                {
                    Name = "policy-path",
                    DisableAllLoot = true,
                    Points = new List<PathPoint>
                    {
                        new(100, 100, mode: BotMode.MoveAndAttack),
                        new(101, 100, mode: BotMode.MoveAndAttackAndLoot)
                    }
                };
                File.WriteAllText(fullPath, JsonSerializer.Serialize(segment));

                var log = new List<string>();
                var loader = new SavedPathLoader(log.Add);
                List<Waypoint>? waypoints = loader.LoadSegment(fileName);

                Assert.NotNull(waypoints);
                Assert.Equal(2, waypoints!.Count);
                Assert.All(waypoints, waypoint => Assert.True(waypoint.DisableAllLoot));
            }
            finally
            {
                try { File.Delete(fullPath); } catch { }
            }
        }

        [Fact]
        public async Task PathRunnerAppliesTheProfileOverrideBeforeTheFirstMovementTick()
        {
            var log = new List<string>();
            var runner = new PathRunnerService(CreateStubMemory(log.Add), log.Add);
            var waypoints = new List<Waypoint>
            {
                new(100, 100, MovementPrecision.Medium, BotMode.MoveAndAttackAndLoot)
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await runner.RunPathAsync(waypoints, loop: false, cts.Token, disableAllLoot: true);

            Assert.NotNull(runner.CurrentMovement);
            Assert.True(runner.CurrentMovement!.DisableAllLootOverride);
            Assert.True(runner.CurrentMovement.IsAutomaticLootDisabled);
        }

        // ─────────────────────────── effective policy ───────────────────────────

        [Fact]
        public void ProfileAndPathFlagsCombineIntoOneEffectivePolicy()
        {
            var log = new List<string>();
            var profileOnly = CreateMovement(log.Add, disableAllLoot: false, profileOverride: true);
            var pathOnly = CreateMovement(log.Add, disableAllLoot: true, profileOverride: false);
            var enabled = CreateMovement(log.Add, disableAllLoot: false, profileOverride: false);

            Assert.True(profileOnly.IsAutomaticLootDisabled);
            Assert.True(pathOnly.IsAutomaticLootDisabled);
            Assert.False(enabled.IsAutomaticLootDisabled);
        }

        [Fact]
        public void DisabledLootDoesNotTurnAttackAndLootIntoOnlyMove()
        {
            var log = new List<string>();

            foreach (BotMode mode in new[] { BotMode.MoveAndAttack, BotMode.MoveAndAttackAndLoot })
            {
                var movement = CreateMovement(log.Add, mode, disableAllLoot: true);
                Assert.Equal(mode, movement.CurrentMode);
                Assert.False(movement.IsMoveOnlyActive);
            }

            var moveOnly = CreateMovement(log.Add, BotMode.OnlyMove, disableAllLoot: true);
            Assert.Equal(BotMode.OnlyMove, moveOnly.CurrentMode);
            Assert.True(moveOnly.IsMoveOnlyActive);
        }

        [Fact]
        public void MovementSynchronizesTheEffectivePolicyIntoTheLootSystem()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            var movement = CreateMovement(log.Add, loot: loot);

            InvokePrivate(movement, "SynchronizeAutomaticLootPolicy");
            Assert.False(loot.AutomaticLootDisabled);

            movement.DisableAllLootOverride = true;
            InvokePrivate(movement, "SynchronizeAutomaticLootPolicy");
            Assert.True(loot.AutomaticLootDisabled);

            movement.DisableAllLootOverride = false;
            InvokePrivate(movement, "SynchronizeAutomaticLootPolicy");
            Assert.False(loot.AutomaticLootDisabled);
        }

        [Fact]
        public void FinalWaypointStandbyKeepsThePathPolicy()
        {
            var log = new List<string>();
            var disabled = CreateMovement(log.Add, disableAllLoot: true);

            Assert.True(InvokeAdvanceAsync(disabled, 1000, 1000).GetAwaiter().GetResult());
            Assert.True((bool)GetPrivateField(disabled, "_finalStandbyActive")!);
            Assert.True(disabled.IsAutomaticLootDisabled);
        }

        [Fact]
        public void FinalWaypointStandbyAlsoHonoursTheProfileOverride()
        {
            var log = new List<string>();
            var movement = CreateMovement(log.Add, disableAllLoot: false, profileOverride: true);

            Assert.True(InvokeAdvanceAsync(movement, 1000, 1000).GetAwaiter().GetResult());
            Assert.True((bool)GetPrivateField(movement, "_finalStandbyActive")!);
            Assert.True(movement.IsAutomaticLootDisabled);
        }

        // ─────────────────────────── navigation safety ───────────────────────────

        [Fact]
        public void DisabledMoveAndAttackAndLootNeverArmsAFatalMissingScannerBarrier()
        {
            var log = new List<string>();
            var movement = CreateMovement(log.Add, BotMode.MoveAndAttackAndLoot, disableAllLoot: true);

            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);

            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(movement.IsMandatoryLootBarrierActive);
            Assert.False(BarrierHoldsInput(movement));
            Assert.DoesNotContain(log, line => line.Contains("FATAL", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("LootBarrier] Armed", StringComparison.Ordinal));
        }

        [Fact]
        public async Task DisabledLootNeverHoldsNavigationForAPendingObligation()
        {
            var log = new List<string>();
            var movement = CreateMovement(log.Add, BotMode.MoveAndAttackAndLoot, disableAllLoot: true);
            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);

            MethodInfo method = typeof(MovementSystem).GetMethod(
                "HoldNavigationForMandatoryLootAsync", PrivateInstance)
                ?? throw new InvalidOperationException("HoldNavigationForMandatoryLootAsync was not found.");
            var task = (Task<bool>?)method.Invoke(movement, new object[] { CancellationToken.None });

            Assert.NotNull(task);
            Assert.False(await task!);
        }

        [Fact]
        public void SwitchingToDisabledCancelsThePendingObligationAndReleasesInput()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            var movement = CreateMovement(log.Add, loot: loot);

            long firstRequest = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            Assert.True(firstRequest > 0);
            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            Assert.True(movement.IsLootNavigationBlocked);
            Assert.True(BarrierHoldsInput(movement));

            movement.DisableAllLootOverride = true;
            InvokePrivate(movement, "RefreshLootBarrier");

            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.True(loot.TryGetMandatoryLootRequestStatus(firstRequest, out LootRequestStatus cancelled));
            Assert.Equal(LootRequestState.Cancelled, cancelled.State);
        }

        [Fact]
        public void SwitchingBackToEnabledResumesNormalObligationHandling()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            var movement = CreateMovement(log.Add, loot: loot);

            long firstRequest = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            movement.DisableAllLootOverride = true;
            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.False(movement.IsLootNavigationBlocked);

            movement.DisableAllLootOverride = false;
            long secondRequest = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);

            Assert.True(secondRequest > firstRequest);
            Assert.True(movement.IsLootNavigationBlocked);
            Assert.Equal(secondRequest, movement.LootBarrier.RequestId);
            Assert.True(loot.TryGetMandatoryLootRequestStatus(secondRequest, out LootRequestStatus pending));
            Assert.Equal(LootRequestState.Pending, pending.State);
        }

        [Fact]
        public async Task DisabledLootDoesNotBlockWaypointAdvancementOrGrouping()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;
            var movement = CreateMovement(log.Add, disableAllLoot: true, loot: loot);

            // Even stale loot flags must not turn into a grouping/advancement hold.
            SetPrivateField(loot, "_isCollecting", true);
            SetPrivateField(loot, "_pinkScanRequested", true);

            Assert.False(movement.IsMobGroupingLootHoldActive);
            Assert.False(movement.IsMandatoryLootBarrierActive);
            Assert.True(await InvokeAdvanceAsync(movement, 1000, 1000));
        }

        [Fact]
        public void DisabledLootNeverSuppressesPeriodicTargetCycling()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;
            SetPrivateField(loot, "_pinkScanRequested", true);
            SetPrivateField(loot, "_pinkScanOwnsCameraCursor", true);

            bool executed = false;
            bool ran = loot.TryRunMovementTargetAction(() => executed = true);

            Assert.True(ran);
            Assert.True(executed);
        }

        // ─────────────────────────── no automatic loot work ───────────────────────────

        [Fact]
        public async Task DisabledLootUpdatePerformsNoCaptureOrLootWork()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;

            await loot.Update(CancellationToken.None);

            Assert.False(loot.IsLootCycleActive);
            Assert.False(loot.IsCollecting);
            Assert.False(loot.IsPinkScanPendingOrActive);
            Assert.False(loot.IsPinkRescanActive);
            Assert.DoesNotContain(log, line => line.Contains("[LootCapture]", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("[LootCamera]", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("[LootSS]", StringComparison.Ordinal));
        }

        [Fact]
        public void DisabledLootRefusesMandatoryRequestsAndPinkRequests()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;
            loot.PinkLootOnlyMode = true;

            Assert.Equal(0, loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill));
            Assert.False(loot.HasActiveMandatoryLootRequest);

            loot.RequestPinkScan();
            loot.SchedulePinkRescanAfterKill();

            Assert.False(loot.IsPinkScanPendingOrActive);
            Assert.False(loot.IsPinkRescanActive);
            var scheduler = GetPrivateField(loot, "_pinkRescanScheduler")!;
            var snapshot = (PinkRescanSnapshot)scheduler.GetType()
                .GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(scheduler)!;
            Assert.False(snapshot.Pending);
            Assert.False(snapshot.Active);
        }

        [Fact]
        public void DisabledLootNeverStartsThePassivePreScan()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;
            SetPrivateField(loot, "_nextPassivePreScanAt", DateTime.MinValue);

            InvokePrivate(loot, "RunPassiveLootPreScanIfDue");

            Assert.Equal(DateTime.MinValue, (DateTime)GetPrivateField(loot, "_nextPassivePreScanAt")!);
            Assert.DoesNotContain(log, line => line.Contains("[LootPreScan]", StringComparison.Ordinal));
        }

        [Fact]
        public void DisabledLootRefusesCameraSetupAndScanPasses()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;

            var cameraArgs = new object?[] { null };
            MethodInfo ensureCamera = typeof(LootSystem).GetMethod("EnsureLootScanCamera", PrivateInstance)
                ?? throw new InvalidOperationException("EnsureLootScanCamera was not found.");
            bool cameraReady = (bool)ensureCamera.Invoke(loot, cameraArgs)!;

            Assert.False(cameraReady);
            Assert.Contains("disabled", (string)cameraArgs[0]!, StringComparison.OrdinalIgnoreCase);
            Assert.False((bool)GetPrivateField(loot, "_lootScanCameraOwned")!);

            var scan = InvokePrivateResult(
                loot, "ScanRegion",
                BotConstants.Loot.BigScanX,
                BotConstants.Loot.BigScanY,
                "BigScan",
                LootSystem.LootScanMode.NormalCombined,
                "test",
                true);
            Assert.Equal(LootSystem.LootScanPassResult.Cancelled, (LootSystem.LootScanPassResult)scan!);
            Assert.DoesNotContain(log, line => line.Contains("Collecting candidate", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("[LootSS]", StringComparison.Ordinal));
        }

        [Fact]
        public void DisabledLootNeverStartsAnItemCollection()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;

            InvokePrivate(loot, "CollectionClick");

            Assert.False(loot.IsCollecting);
            Assert.DoesNotContain(log, line => line.Contains("COLLECTION START", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("Collecting item", StringComparison.Ordinal));
        }

        [Fact]
        public void DisablingCancelsPendingPinkWorkAndReleasesOwnership()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            SetPrivateField(loot, "_pinkScanRequested", true);
            SetPrivateField(loot, "_isCollecting", true);
            SetPrivateField(loot, "_lootScanCameraOwned", true);

            loot.AutomaticLootDisabled = true;

            Assert.False(loot.IsPinkScanPendingOrActive);
            Assert.False(loot.IsCollecting);
            Assert.False((bool)GetPrivateField(loot, "_lootScanCameraOwned")!);
            Assert.True(loot.TryRunMovementTargetAction(() => { }));
        }

        // ─────────────────────────── player safety ───────────────────────────

        [Fact]
        public void PlayerMarkerSafetyResponseStillFiresWhileLootIsDisabled()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;
            int detected = 0;
            loot.OtherPlayerMarkerDetected = () => detected++;

            var candidate = new PlayerMarkerCandidate(
                "Empire", new Rectangle(10, 10, 20, 20), 120, 0.5f, 1.2f, new PointF(20, 20));
            loot.ReportImmediateOtherPlayerMarker(candidate, "test");

            Assert.Equal(1, detected);
            Assert.Contains(log, line => line.Contains("other-player marker detected", StringComparison.Ordinal));
        }

        [Fact]
        public void PlayerMarkerSafetyRespectsTheExistingStopPolicyWhileLootIsDisabled()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.AutomaticLootDisabled = true;
            loot.ShouldStopOnOtherPlayerMarker = () => false;
            int detected = 0;
            loot.OtherPlayerMarkerDetected = () => detected++;

            var candidate = new PlayerMarkerCandidate(
                "Alliance", new Rectangle(10, 10, 20, 20), 120, 0.5f, 1.2f, new PointF(20, 20));
            loot.ReportImmediateOtherPlayerMarker(candidate, "test");

            Assert.Equal(0, detected);
            Assert.Contains(log, line => line.Contains("Stop/return-to-city behavior is disabled", StringComparison.Ordinal));
        }
    }
}
