using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the central combat target classification/attack permission
    /// (the 2026-10-29 incident: the bot attacked another player whose TargetId=718 was below
    /// the legacy mob/player id boundary).
    ///
    /// The rule under test: only a CONFIRMED monster may be attacked. A player-domain id, a
    /// confirmed other-player marker on screen, an in-city (NPC) target, an unreadable/stale
    /// other-player scan and an invalid target all forbid the attack.
    /// </summary>
    public sealed class CombatTargetSafetyTests
    {
        [Fact]
        public void ConfirmedMonsterIsAttackable()
        {
            var scanner = new StubCombatPlayerPresenceScanner();
            var safety = CombatSafetyFixtures.WithPresence(() => 5000, scanner);

            CombatTargetValidation validation = safety.Validate();

            Assert.True(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.Monster, validation.Kind);
            Assert.Equal(5000, validation.TargetId);
            Assert.Equal(1, scanner.ObserveCount);
        }

        [Fact]
        public void PlayerDomainTargetIdIsRejectedWithoutScanning()
        {
            var scanner = new StubCombatPlayerPresenceScanner();
            var safety = CombatSafetyFixtures.WithPresence(
                () => BotConstants.Combat.MaxMobTargetId + 1, scanner);

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.OtherPlayer, validation.Kind);
            Assert.Equal("target-id-player-domain", validation.Source);
            Assert.Equal(0, scanner.ObserveCount);
        }

        /// <summary>
        /// The exact incident fixture: TargetId=718 is BELOW the legacy mob boundary, so the id
        /// alone must not prove a monster. The verified player-type evidence (an Alliance/green
        /// faction marker component near the character, the same shape family the production
        /// pixel detector reports) must forbid the attack.
        /// </summary>
        [Fact]
        public void TargetId718WithConfirmedPlayerMarkerIsRejected()
        {
            var marker = StubCombatPlayerPresenceScanner.PlayerMarkerAt(new PointF(449f, 321f));
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = marker,
                Detail = "faction marker Alliance/green distanceFromCharacter=89px"
            };
            var safety = CombatSafetyFixtures.WithPresence(() => 718, scanner);

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.OtherPlayer, validation.Kind);
            Assert.Equal("other-player-marker", validation.Source);
            Assert.Equal(718, validation.TargetId);
            Assert.NotNull(validation.PlayerMarker);
            Assert.Equal("Alliance/green", validation.PlayerMarker!.Value.Faction);
        }

        [Fact]
        public void PlayerVisibleOnScreenBlocksAttackEvenWhenTheSelectedIdLooksLikeAMob()
        {
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = StubCombatPlayerPresenceScanner.PlayerMarkerAt(new PointF(450f, 320f))
            };
            var safety = CombatSafetyFixtures.WithPresence(() => 20005, scanner);

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.OtherPlayer, validation.Kind);
        }

        [Fact]
        public void UnavailablePlayerScanProducesUnknownAndBlocksTheAttack()
        {
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.Unavailable,
                Detail = "game client frame could not be captured"
            };
            var safety = CombatSafetyFixtures.WithPresence(() => 5000, scanner);

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.Unknown, validation.Kind);
            Assert.Contains("could not be captured", validation.Reason);
        }

        [Fact]
        public void StalePlayerEvidenceProducesUnknownAndBlocksTheAttack()
        {
            var scanner = new StubCombatPlayerPresenceScanner
            {
                CapturedAtUtc = DateTime.UtcNow.AddSeconds(-30)
            };
            var safety = CombatSafetyFixtures.WithPresence(() => 5000, scanner);

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.Unknown, validation.Kind);
            Assert.Contains("stale", validation.Reason);
        }

        [Fact]
        public void InCityTargetIsNotAttackable()
        {
            var safety = CombatSafetyFixtures.Create(
                () => 5000, new StubCombatPlayerPresenceScanner(), inCity: true);

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.Npc, validation.Kind);
            Assert.Equal("in-city", validation.Source);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NoTargetIsNotAttackable(int targetId)
        {
            var safety = CombatSafetyFixtures.WithPresence(
                () => targetId, new StubCombatPlayerPresenceScanner());

            CombatTargetValidation validation = safety.Validate();

            Assert.False(validation.AttackAllowed);
            Assert.Equal(TargetEntityKind.None, validation.Kind);
        }

        [Fact]
        public void NewTargetForcesAFreshPlayerScan()
        {
            int target = 5000;
            var scanner = new StubCombatPlayerPresenceScanner();
            var safety = CombatSafetyFixtures.WithPresence(() => target, scanner);

            safety.Validate();
            safety.Validate();
            target = 718;
            safety.Validate();

            Assert.Equal(new[] { true, false, true }, scanner.ForceCaptureRequests);
        }

        [Fact]
        public void AttackBlockedAndTargetValidationAreLoggedOnTransitionsOnly()
        {
            var lines = new List<string>();
            var scanner = new StubCombatPlayerPresenceScanner();
            var safety = new CombatTargetSafety(
                () => 718,
                scanner,
                () => false,
                lines.Add);
            scanner.Status = CombatPlayerPresenceStatus.OtherPlayer;
            scanner.Candidate = StubCombatPlayerPresenceScanner.PlayerMarkerAt(new PointF(449f, 321f));

            safety.Validate();
            safety.Validate();
            safety.Validate();

            Assert.Single(lines, line => line.Contains("[TargetValidation] TargetId=718 Type=OtherPlayer"));
            Assert.Single(lines, line => line.Contains("[CombatSafety] AttackBlocked TargetId=718 Reason=OtherPlayer"));
        }

        [Fact]
        public void ConfirmedMonsterIsNotLoggedAsBlocked()
        {
            var lines = new List<string>();
            var safety = new CombatTargetSafety(
                () => 20005,
                new StubCombatPlayerPresenceScanner(),
                () => false,
                lines.Add);

            Assert.True(safety.Validate().AttackAllowed);
            Assert.DoesNotContain(lines, line => line.Contains("[CombatSafety] AttackBlocked"));
        }
    }
}
