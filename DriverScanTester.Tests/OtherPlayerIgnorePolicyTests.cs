using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the other-player faction ignore options ("Ignore Alliance
    /// players (green)" / "Ignore Empire players (yellow)"). Both default to false, so the
    /// original behavior (any confirmed faction marker stops the bot and blocks attacks) is
    /// preserved unless the host checks the matching option.
    /// </summary>
    public sealed class OtherPlayerIgnorePolicyTests
    {
        // ─────────────────────────── faction mapping ───────────────────────────

        [Theory]
        [InlineData("Alliance/green", true, false, true)]
        [InlineData("Alliance", true, false, true)]
        [InlineData("Empire/yellow", true, false, false)]
        [InlineData("Empire/yellow", false, true, true)]
        [InlineData("Empire", false, true, true)]
        [InlineData("Alliance/green", false, true, false)]
        [InlineData("Alliance/green", false, false, false)]
        [InlineData("Empire/yellow", false, false, false)]
        public void ShouldIgnoreMapsTheFactionLabelToTheMatchingOption(
            string faction, bool ignoreAlliance, bool ignoreEmpire, bool expected)
        {
            Assert.Equal(expected,
                OtherPlayerIgnoreSettings.ShouldIgnore(faction, ignoreAlliance, ignoreEmpire));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Other/red")]
        public void ShouldIgnoreNeverIgnoresAnUnknownOrEmptyFaction(string? faction)
        {
            Assert.False(OtherPlayerIgnoreSettings.ShouldIgnore(faction, true, true));
        }

        // ───────────────────────── immediate report path ─────────────────────────

        [Fact]
        public void ImmediateReportOfAnIgnoredFactionDoesNotStopTheBot()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.ShouldIgnoreOtherPlayerMarker = candidate => OtherPlayerIgnoreSettings.ShouldIgnore(
                candidate.Faction, ignoreAlliancePlayers: true, ignoreEmpirePlayers: false);
            int detected = 0;
            loot.OtherPlayerMarkerDetected = () => detected++;

            loot.ReportImmediateOtherPlayerMarker(AllianceMarker(), "test");

            Assert.Equal(0, detected);
            Assert.Contains(log, line => line.Contains("Alliance/green faction is ignored", StringComparison.Ordinal));
        }

        [Fact]
        public void ImmediateReportOfANonIgnoredFactionStillStopsTheBot()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.ShouldIgnoreOtherPlayerMarker = candidate => OtherPlayerIgnoreSettings.ShouldIgnore(
                candidate.Faction, ignoreAlliancePlayers: true, ignoreEmpirePlayers: false);
            int detected = 0;
            loot.OtherPlayerMarkerDetected = () => detected++;

            loot.ReportImmediateOtherPlayerMarker(EmpireMarker(), "test");

            Assert.Equal(1, detected);
        }

        [Fact]
        public void ImmediateReportOfAnIgnoredEmpireMarkerKeepsWorkingWhenOnlyEmpireIsIgnored()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.ShouldIgnoreOtherPlayerMarker = candidate => OtherPlayerIgnoreSettings.ShouldIgnore(
                candidate.Faction, ignoreAlliancePlayers: false, ignoreEmpirePlayers: true);
            int detected = 0;
            loot.OtherPlayerMarkerDetected = () => detected++;

            loot.ReportImmediateOtherPlayerMarker(EmpireMarker(), "test");

            Assert.Equal(0, detected);
            Assert.Contains(log, line => line.Contains("Empire/yellow faction is ignored", StringComparison.Ordinal));
        }

        // ───────────────────────── periodic loot-scan path ─────────────────────────

        [Fact]
        public void PeriodicMarkerScanDoesNotOpenConfirmationForAnIgnoredFaction()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.PlayerMarkerScanEnabled = true;
            loot.ShouldIgnoreOtherPlayerMarker = _ => true;
            SetPrivateField(loot, "_bitmap", CreateEmpireMarkerFrame());
            SetPrivateField(loot, "_clientWidth", 320);
            SetPrivateField(loot, "_clientHeight", 240);

            InvokePrivate(loot, "ScanForOtherPlayerPixels");

            Assert.False((bool)GetPrivateField(loot, "_playerMarkerConfirmationPending")!);
            Assert.Equal(0, (int)GetPrivateField(loot, "_playerMarkerChecksConfirmed")!);
            Assert.Contains(log, line => line.Contains("belongs to an ignored faction", StringComparison.Ordinal));
        }

        [Fact]
        public void PeriodicMarkerScanStillOpensConfirmationForANonIgnoredFaction()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.PlayerMarkerScanEnabled = true;
            loot.ShouldIgnoreOtherPlayerMarker = _ => false;
            SetPrivateField(loot, "_bitmap", CreateEmpireMarkerFrame());
            SetPrivateField(loot, "_clientWidth", 320);
            SetPrivateField(loot, "_clientHeight", 240);

            InvokePrivate(loot, "ScanForOtherPlayerPixels");

            Assert.True((bool)GetPrivateField(loot, "_playerMarkerConfirmationPending")!);
            Assert.Equal(1, (int)GetPrivateField(loot, "_playerMarkerChecksConfirmed")!);
        }

        // ───────────────────── combat-time presence scanner ─────────────────────

        [Fact]
        public void CombatPresenceScannerTreatsAnIgnoredEmpireMarkerAsNoPlayer()
        {
            Bitmap frame = CreateEmpireMarkerFrame();
            var scanner = new ScreenCombatPlayerPresenceScanner(
                _ => { },
                captureFrame: () => frame,
                shouldIgnoreOtherPlayer: candidate => OtherPlayerIgnoreSettings.ShouldIgnore(
                    candidate.Faction, ignoreAlliancePlayers: false, ignoreEmpirePlayers: true));

            CombatPlayerPresence presence = scanner.Observe(forceCapture: true);

            // An ignored faction must look exactly like an empty screen so the combat
            // validation keeps authorizing attacks; the real-scanner non-ignored path is
            // covered by the periodic loot-scan tests above.
            Assert.Equal(CombatPlayerPresenceStatus.NoOtherPlayer, presence.Status);
            Assert.Contains("ignored", presence.Detail, StringComparison.OrdinalIgnoreCase);
        }

        // ───────────────────── combat target validation ─────────────────────

        [Fact]
        public void TargetValidationAllowsTheAttackWhenTheVisibleFactionIsIgnored()
        {
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = EmpireMarker()
            };
            var safety = new CombatTargetSafety(
                () => 20005,
                scanner,
                () => false,
                _ => { },
                shouldIgnoreOtherPlayer: candidate => OtherPlayerIgnoreSettings.ShouldIgnore(
                    candidate.Faction, ignoreAlliancePlayers: false, ignoreEmpirePlayers: true));

            CombatTargetValidation validation = safety.Validate();

            Assert.True(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.Monster, validation.Kind);
            Assert.Contains("ignored", validation.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TargetValidationStillBlocksTheAttackForANonIgnoredFaction()
        {
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = EmpireMarker()
            };
            var safety = new CombatTargetSafety(
                () => 20005,
                scanner,
                () => false,
                _ => { },
                shouldIgnoreOtherPlayer: candidate => OtherPlayerIgnoreSettings.ShouldIgnore(
                    candidate.Faction, ignoreAlliancePlayers: true, ignoreEmpirePlayers: false));

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.OtherPlayer, validation.Kind);
        }

        [Fact]
        public void PlayerIdDomainTargetsStayBlockedEvenForAnIgnoredFaction()
        {
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = EmpireMarker()
            };
            var safety = new CombatTargetSafety(
                () => BotConstants.Combat.MaxMobTargetId + 1,
                scanner,
                () => false,
                _ => { },
                shouldIgnoreOtherPlayer: _ => true);

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.OtherPlayer, validation.Kind);
        }

        // ───────────────────────── profile-level options ─────────────────────────

        [Fact]
        public void BotProfileDefaultsToNotIgnoringAnyFaction()
        {
            var profile = new BotProfile();

            Assert.False(profile.IgnoreAlliancePlayers);
            Assert.False(profile.IgnoreEmpirePlayers);
        }

        [Fact]
        public void ExistingProfileJsonWithoutTheFieldsLoadsAsFalse()
        {
            BotProfile? profile = JsonSerializer.Deserialize<BotProfile>(
                "{\"Name\":\"legacy\",\"FlowSteps\":[{\"Type\":1,\"PathFile\":\"old.json\"}]}");

            Assert.NotNull(profile);
            Assert.False(profile!.IgnoreAlliancePlayers);
            Assert.False(profile.IgnoreEmpirePlayers);
        }

        [Fact]
        public void ProfileIgnoreFlagsSurviveTheLoaderSaveLoadRoundTrip()
        {
            Directory.CreateDirectory(ProfileDirectory);
            string name = $"IgnorePlayers_{Guid.NewGuid():N}";
            string path = Path.Combine(ProfileDirectory, name + ".json");

            try
            {
                var loader = new BotProfileLoader(_ => { });
                var profile = new BotProfile
                {
                    Name = name,
                    IgnoreAlliancePlayers = true,
                    IgnoreEmpirePlayers = true,
                    FlowSteps = new List<BotFlowStep>
                    {
                        new() { Type = BotFlowStepType.Operation, OperationName = "GoInsideCOT" }
                    }
                };

                Assert.True(loader.SaveProfile(profile));
                BotProfile? loaded = loader.LoadProfile(name);

                Assert.NotNull(loaded);
                Assert.True(loaded!.IgnoreAlliancePlayers);
                Assert.True(loaded.IgnoreEmpirePlayers);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void ProfileLevelOverrideIgnoresAFactionWithoutAPathFlag()
        {
            var path = new List<Waypoint>
            {
                new(1000, 1000, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };
            var movement = new MovementSystem(CreateStubMemory(), _ => { }, 1000, 1000, customPath: path)
            {
                IgnoreAlliancePlayersOverride = true
            };

            Assert.True(movement.ShouldIgnoreOtherPlayerMarker(AllianceMarker()));
            Assert.False(movement.ShouldIgnoreOtherPlayerMarker(EmpireMarker()));
        }

        [Fact]
        public async Task PathRunnerPropagatesTheProfileIgnoreFlagsToTheMovement()
        {
            var log = new List<string>();
            var runner = new PathRunnerService(CreateStubMemory(), log.Add);
            var waypoints = new List<Waypoint>
            {
                new(100, 100, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await runner.RunPathAsync(
                waypoints, loop: false, cts.Token,
                ignoreAlliancePlayers: true,
                ignoreEmpirePlayers: false);

            Assert.NotNull(runner.CurrentMovement);
            Assert.True(runner.CurrentMovement!.IgnoreAlliancePlayersOverride);
            Assert.False(runner.CurrentMovement.IgnoreEmpirePlayersOverride);
        }

        // ─────────────────────── path-level (Path Editor) options ───────────────────────

        [Fact]
        public void PathSegmentDefaultsToNotIgnoringAnyFaction()
        {
            var segment = new PathSegment();

            Assert.False(segment.IgnoreAlliancePlayers);
            Assert.False(segment.IgnoreEmpirePlayers);
        }

        [Fact]
        public void PathSegmentFlagsSurviveAJsonRoundTrip()
        {
            var segment = new PathSegment
            {
                Name = "ignore-path",
                IgnoreAlliancePlayers = true,
                IgnoreEmpirePlayers = true,
                Points = new List<PathPoint> { new(100, 100, mode: BotMode.MoveAndAttack) }
            };

            PathSegment? loaded = JsonSerializer.Deserialize<PathSegment>(JsonSerializer.Serialize(segment));

            Assert.NotNull(loaded);
            Assert.True(loaded!.IgnoreAlliancePlayers);
            Assert.True(loaded.IgnoreEmpirePlayers);
        }

        [Fact]
        public void SavedPathLoaderPropagatesThePathFlagsToEveryRuntimeWaypoint()
        {
            Directory.CreateDirectory(SaveDirectory);
            string fileName = $"IgnorePlayers_{Guid.NewGuid():N}.json";
            string fullPath = Path.Combine(SaveDirectory, fileName);

            try
            {
                var segment = new PathSegment
                {
                    Name = "ignore-path",
                    IgnoreAlliancePlayers = true,
                    IgnoreEmpirePlayers = false,
                    Points = new List<PathPoint>
                    {
                        new(100, 100, mode: BotMode.MoveAndAttack),
                        new(101, 100, mode: BotMode.MoveAndAttackAndLoot)
                    }
                };
                File.WriteAllText(fullPath, JsonSerializer.Serialize(segment));

                var log = new List<string>();
                List<Waypoint>? waypoints = new SavedPathLoader(log.Add).LoadSegment(fileName);

                Assert.NotNull(waypoints);
                Assert.All(waypoints!, waypoint => Assert.True(waypoint.IgnoreAlliancePlayers));
                Assert.All(waypoints!, waypoint => Assert.False(waypoint.IgnoreEmpirePlayers));
            }
            finally
            {
                try { File.Delete(fullPath); } catch { }
            }
        }

        [Fact]
        public void MovementSystemAppliesThePathFlagToTheActiveWaypoint()
        {
            var path = new List<Waypoint>
            {
                new(1000, 1000, MovementPrecision.Medium, BotMode.MoveAndAttack,
                    ignoreAlliancePlayers: true, ignoreEmpirePlayers: false)
            };
            var movement = new MovementSystem(CreateStubMemory(), _ => { }, 1000, 1000, customPath: path);

            Assert.True(movement.ShouldIgnoreOtherPlayerMarker(AllianceMarker()));
            Assert.False(movement.ShouldIgnoreOtherPlayerMarker(EmpireMarker()));
        }

        [Fact]
        public void MovementSystemDoesNotIgnoreFactionsWithoutThePathFlagOrTheGlobalOption()
        {
            var path = new List<Waypoint>
            {
                new(1000, 1000, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };
            var movement = new MovementSystem(CreateStubMemory(), _ => { }, 1000, 1000, customPath: path);

            Assert.False(movement.ShouldIgnoreOtherPlayerMarker(AllianceMarker()));
            Assert.False(movement.ShouldIgnoreOtherPlayerMarker(EmpireMarker()));
        }

        // ───────────────────────────── helpers ─────────────────────────────

        private static PlayerMarkerCandidate AllianceMarker()
            => new("Alliance/green", new Rectangle(180, 80, 20, 20), 300, 0.4f, 1.5f, new PointF(190, 90));

        private static PlayerMarkerCandidate EmpireMarker()
            => new("Empire/yellow", new Rectangle(180, 80, 18, 18), 280, 0.7f, 1.0f, new PointF(189, 89));

        private static LootSystem CreateLoot(Action<string> log)
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
            return new LootSystem(new GameMemoryService(0, read, write, 0, 8, log), log);
        }

        private static string ProfileDirectory => Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedBotProfiles"));

        private static string SaveDirectory => Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));

        private static GameMemoryService CreateStubMemory()
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
            return new GameMemoryService(0, read, write, 0, 8, _ => { });
        }

        /// <summary>320x240 client frame with a valid Empire (yellow) ground marker.</summary>
        private static Bitmap CreateEmpireMarkerFrame()
        {
            Bitmap frame = new(320, 240, PixelFormat.Format32bppArgb);
            FillRectangle(frame, new Rectangle(210, 90, 14, 14), Color.FromArgb(255, 255, 0));
            return frame;
        }

        private static void FillRectangle(Bitmap bitmap, Rectangle rectangle, Color color)
        {
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
            for (int x = rectangle.Left; x < rectangle.Right; x++)
                bitmap.SetPixel(x, y, color);
        }

        private static void InvokePrivate(object target, string method, params object?[] args)
        {
            System.Reflection.MethodInfo info = target.GetType().GetMethod(
                method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Method {method} was not found on {target.GetType().Name}.");
            info.Invoke(target, args);
        }

        private static object? GetPrivateField(object target, string name)
        {
            System.Reflection.FieldInfo field = target.GetType().GetField(
                name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Field {name} was not found on {target.GetType().Name}.");
            return field.GetValue(target);
        }

        private static void SetPrivateField(object target, string name, object? value)
        {
            System.Reflection.FieldInfo field = target.GetType().GetField(
                name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Field {name} was not found on {target.GetType().Name}.");
            field.SetValue(target, value);
        }
    }
}
