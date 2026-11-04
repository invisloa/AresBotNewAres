using System;
using System.Threading;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Global runtime policy for other-player faction markers (Empire / yellow and
    /// Alliance / green). Both flags default to false, which keeps the original
    /// behavior: any confirmed faction marker stops the bot and blocks attacks.
    ///
    /// When a faction is ignored, its marker is treated as if that player were not on
    /// screen: the confirmation pipeline does not stop the EXP loop / return to the city
    /// and the combat target safety no longer blocks the attack. An actual PLAYER target
    /// id (the player-id domain) is still always rejected — ignoring a faction must never
    /// turn into "attack players".
    ///
    /// This follows the existing global runtime-option pattern of
    /// <see cref="MobGroupingRuntimeSettings"/>: one process-wide switch read
    /// live by every LootSystem / CombatTargetSafety instance, so a toggle applies
    /// immediately to a running workflow.
    /// </summary>
    public static class OtherPlayerIgnoreSettings
    {
        private const string AllianceFactionPrefix = "Alliance";
        private const string EmpireFactionPrefix = "Empire";

        private static int _ignoreAlliancePlayers;
        private static int _ignoreEmpirePlayers;

        /// <summary>When true, Alliance (green) faction markers are ignored.</summary>
        public static bool IgnoreAlliancePlayers
        {
            get => Volatile.Read(ref _ignoreAlliancePlayers) != 0;
            set => Volatile.Write(ref _ignoreAlliancePlayers, value ? 1 : 0);
        }

        /// <summary>When true, Empire (yellow) faction markers are ignored.</summary>
        public static bool IgnoreEmpirePlayers
        {
            get => Volatile.Read(ref _ignoreEmpirePlayers) != 0;
            set => Volatile.Write(ref _ignoreEmpirePlayers, value ? 1 : 0);
        }

        /// <summary>Process-wide default policy used when no instance override is set.</summary>
        public static bool ShouldIgnore(PlayerMarkerCandidate candidate)
            => ShouldIgnore(candidate.Faction, IgnoreAlliancePlayers, IgnoreEmpirePlayers);

        /// <summary>
        /// Pure faction-to-flag mapping. <paramref name="faction"/> is the detector's
        /// faction label ("Alliance/green", "Empire/yellow", or the short "Alliance" /
        /// "Empire" form); an unknown or empty label is never ignored.
        /// </summary>
        public static bool ShouldIgnore(string? faction, bool ignoreAlliancePlayers, bool ignoreEmpirePlayers)
        {
            if (string.IsNullOrWhiteSpace(faction))
                return false;

            string label = faction!;
            if (ignoreAlliancePlayers &&
                label.StartsWith(AllianceFactionPrefix, StringComparison.OrdinalIgnoreCase))
                return true;

            return ignoreEmpirePlayers &&
                   label.StartsWith(EmpireFactionPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
