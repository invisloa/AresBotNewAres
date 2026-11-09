using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// The NPC and item MouseOver values change together with the game launch. An automatic
    /// NPC calibration must therefore request the item value re-discovery, scoped to the
    /// game session it happened in.
    /// </summary>
    public sealed class MouseOverCalibrationSessionTests
    {
        private static readonly (uint, ulong) SessionA = (1234u, 0x400000UL);
        private static readonly (uint, ulong) SessionB = (5678u, 0x500000UL);

        [Fact]
        public void NpcCalibrationRequestsItemDiscoveryUntilTheItemValueIsKnown()
        {
            var session = new MouseOverCalibrationSession();

            Assert.False(session.NeedsItemValueDiscovery(SessionA));

            session.MarkNpcValueDiscovered(SessionA);

            Assert.True(session.WasNpcValueDiscovered(SessionA));
            Assert.False(session.WasItemValueDiscovered(SessionA));
            Assert.True(session.NeedsItemValueDiscovery(SessionA));

            session.MarkItemValueDiscovered(SessionA);

            Assert.True(session.WasItemValueDiscovered(SessionA));
            Assert.False(session.NeedsItemValueDiscovery(SessionA));
        }

        [Fact]
        public void ItemValueKnownWithoutAnNpcCalibrationDoesNotRequestDiscovery()
        {
            var session = new MouseOverCalibrationSession();

            session.MarkItemValueDiscovered(SessionA);

            Assert.False(session.WasNpcValueDiscovered(SessionA));
            Assert.True(session.WasItemValueDiscovered(SessionA));
            Assert.False(session.NeedsItemValueDiscovery(SessionA));

            // A later NPC calibration in the same session must request it again? No — the item
            // value is already known for this session, so there is nothing to re-discover.
            session.MarkNpcValueDiscovered(SessionA);

            Assert.True(session.WasNpcValueDiscovered(SessionA));
            Assert.False(session.NeedsItemValueDiscovery(SessionA));
        }

        [Fact]
        public void AnotherGameSessionNeverInheritsTheMarkers()
        {
            var session = new MouseOverCalibrationSession();

            session.MarkNpcValueDiscovered(SessionA);
            session.MarkItemValueDiscovered(SessionA);

            // The game was restarted: the same app session now sees another identity.
            Assert.False(session.WasNpcValueDiscovered(SessionB));
            Assert.False(session.WasItemValueDiscovered(SessionB));
            Assert.False(session.NeedsItemValueDiscovery(SessionB));

            session.MarkNpcValueDiscovered(SessionB);

            Assert.True(session.NeedsItemValueDiscovery(SessionB));

            // The old session's markers are gone, not resurrected.
            Assert.False(session.WasNpcValueDiscovered(SessionA));
            Assert.False(session.NeedsItemValueDiscovery(SessionA));
        }
    }
}
