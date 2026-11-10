using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Session-scoped MouseOver calibration markers shared by the NPC scan and the loot
    /// system.
    ///
    /// The NPC and item MouseOver values change together when the game is launched. The
    /// NPC scan can prove its value (right-click + dialogue), so a successful automatic
    /// NPC calibration is also the signal that the item value must be re-discovered
    /// (see <see cref="NeedsItemValueDiscovery"/>). The loot system uses that marker for
    /// its immediate trigger and otherwise waits for N consecutive empty loot scans.
    ///
    /// The markers are keyed by the attached game process (pid + module base), so a game
    /// restart within the same application session never inherits another session's state.
    /// Nothing here is persisted: it only describes the currently running game.
    /// </summary>
    public sealed class MouseOverCalibrationSession
    {
        /// <summary>Process-wide markers used by the running bot.</summary>
        public static MouseOverCalibrationSession Current { get; } = new();

        private readonly object _gate = new();
        private (uint ProcessId, ulong ModuleBase) _identity;
        private bool _hasIdentity;
        private bool _npcValueDiscovered;
        private bool _itemValueDiscovered;
        private int _npcCalibrationGeneration;

        /// <summary>
        /// Marks that the NPC MouseOver value was discovered automatically in this game
        /// session (validated by a real right-click + NPC dialogue). Every successful
        /// calibration starts a new generation, so the loot system can discard item-value
        /// observations that were collected before the NPC (and therefore possibly the item)
        /// value changed.
        /// </summary>
        public void MarkNpcValueDiscovered((uint ProcessId, ulong ModuleBase) identity)
        {
            lock (_gate)
            {
                EnsureIdentityLocked(identity);
                _npcValueDiscovered = true;
                _npcCalibrationGeneration++;
            }
        }

        /// <summary>
        /// Marks that the item MouseOver value is known for this game session — either
        /// auto-discovered, confirmed by an actual item pickup, or captured manually with
        /// the Bot window's 'Mouseover Item' button.
        /// </summary>
        public void MarkItemValueDiscovered((uint ProcessId, ulong ModuleBase) identity)
        {
            lock (_gate)
            {
                EnsureIdentityLocked(identity);
                _itemValueDiscovered = true;
            }
        }

        /// <summary>True when the NPC value was auto-calibrated in this game session.</summary>
        public bool WasNpcValueDiscovered((uint ProcessId, ulong ModuleBase) identity)
        {
            lock (_gate)
            {
                EnsureIdentityLocked(identity);
                return _npcValueDiscovered;
            }
        }

        /// <summary>
        /// Monotonic counter of successful NPC auto-calibrations in this game session. It lets
        /// the loot system reset saved item observations after the NPC value (which changes
        /// together with the item value) was re-discovered.
        /// </summary>
        public int NpcCalibrationGeneration((uint ProcessId, ulong ModuleBase) identity)
        {
            lock (_gate)
            {
                EnsureIdentityLocked(identity);
                return _npcCalibrationGeneration;
            }
        }

        /// <summary>True when the item value is already known in this game session.</summary>
        public bool WasItemValueDiscovered((uint ProcessId, ulong ModuleBase) identity)
        {
            lock (_gate)
            {
                EnsureIdentityLocked(identity);
                return _itemValueDiscovered;
            }
        }

        /// <summary>
        /// True when the NPC value was re-discovered in this session but the item value was
        /// not: both values change with the game launch, so the item value is stale too.
        /// </summary>
        public bool NeedsItemValueDiscovery((uint ProcessId, ulong ModuleBase) identity)
        {
            lock (_gate)
            {
                EnsureIdentityLocked(identity);
                return _npcValueDiscovered && !_itemValueDiscovered;
            }
        }

        /// <summary>Forgets the markers when a different game process/launch is observed.</summary>
        private void EnsureIdentityLocked((uint ProcessId, ulong ModuleBase) identity)
        {
            if (_hasIdentity && _identity == identity)
                return;

            _identity = identity;
            _hasIdentity = true;
            _npcValueDiscovered = false;
            _itemValueDiscovered = false;
            _npcCalibrationGeneration = 0;
        }
    }
}
