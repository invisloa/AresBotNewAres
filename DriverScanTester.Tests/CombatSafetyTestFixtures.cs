using System;
using System.Collections.Generic;
using System.Drawing;
using DriverScanTester.Services;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Deterministic other-player evidence source for tests. It never captures the screen, so
    /// the attack-safety regression tests are fast and independent of the desktop.
    /// </summary>
    internal sealed class StubCombatPlayerPresenceScanner : ICombatPlayerPresenceScanner
    {
        public CombatPlayerPresenceStatus Status { get; set; } = CombatPlayerPresenceStatus.NoOtherPlayer;

        public PlayerMarkerCandidate? Candidate { get; set; }

        /// <summary>Evidence timestamp. Null = "captured now"; used to test stale evidence.</summary>
        public DateTime? CapturedAtUtc { get; set; }

        public string Detail { get; set; } = "stub evidence";

        public int ObserveCount { get; private set; }

        /// <summary>Every forceCapture argument received, in order (target-change revalidation proof).</summary>
        public List<bool> ForceCaptureRequests { get; } = new();

        public CombatPlayerPresence Observe(bool forceCapture)
        {
            ObserveCount++;
            ForceCaptureRequests.Add(forceCapture);
            return new CombatPlayerPresence(
                Status,
                Candidate,
                CapturedAtUtc ?? DateTime.UtcNow,
                Detail);
        }

        /// <summary>
        /// Verified player-type fixture: an Alliance/green faction marker component (the same
        /// shape/size family the production pixel detector reports) near the character.
        /// </summary>
        public static PlayerMarkerCandidate PlayerMarkerAt(PointF center)
            => new(
                Faction: "Alliance/green",
                Bounds: new Rectangle((int)center.X - 20, (int)center.Y - 25, 41, 51),
                PixelCount: 939,
                FillRatio: 0.45f,
                AspectRatio: 1.24f,
                Center: center);
    }

    /// <summary>Convenience factory for <see cref="CombatTargetSafety"/> test instances.</summary>
    internal static class CombatSafetyFixtures
    {
        public static CombatTargetSafety Monster(int targetId, bool inCity = false)
            => Create(() => targetId, new StubCombatPlayerPresenceScanner(), inCity);

        public static CombatTargetSafety OtherPlayerTarget(int targetId)
            => Create(() => targetId, new StubCombatPlayerPresenceScanner(), inCity: false);

        public static CombatTargetSafety WithPresence(
            Func<int> readTargetId,
            StubCombatPlayerPresenceScanner scanner,
            bool inCity = false)
            => Create(readTargetId, scanner, inCity);

        public static CombatTargetSafety Create(
            Func<int> readTargetId,
            StubCombatPlayerPresenceScanner scanner,
            bool inCity)
            => new(
                readTargetId,
                scanner,
                () => inCity,
                _ => { },
                maxPresenceAgeMs: BotConstants.CombatSafety.PlayerScanMaxAgeMs);
    }
}
