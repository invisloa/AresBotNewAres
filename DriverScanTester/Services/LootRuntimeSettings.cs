namespace DriverScanTester.Services
{
    /// <summary>
    /// The single process-wide runtime loot-settings container. It is deliberately not
    /// part of profile/path data and is not persisted. Volatile access allows the UI or
    /// a host to toggle options while a loot cycle is running.
    /// </summary>
    public static class LootRuntimeSettings
    {
        private static int _spacebarLootEnabled =
            BotConstants.Loot.SpacebarLootEnabledDefault ? 1 : 0;

        /// <summary>
        /// When false (the default), the loot system never presses the spacebar to loot:
        /// no area-loot presses, no pre-scan presses and no background scan spam.
        /// Pixel-scan mouse probes are unaffected.
        /// </summary>
        public static bool SpacebarLootEnabled
        {
            get => Volatile.Read(ref _spacebarLootEnabled) != 0;
            set => Volatile.Write(ref _spacebarLootEnabled, value ? 1 : 0);
        }
    }
}
