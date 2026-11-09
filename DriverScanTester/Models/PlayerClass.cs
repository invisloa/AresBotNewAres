namespace DriverScanTester.Models
{
    /// <summary>
    /// Character class currently selected/playing, read from
    /// <see cref="BotConstants.MemoryOffsets.CurrentClassPlayer"/>
    /// (<c>[Ares.exe + 0x489BC8] + 0xA9C</c>, 32-bit int; lock-tool name
    /// M_CurrentCLassPlayer).
    ///
    /// The numbering matches the legacy AresTrainerV3_oldBot mapping:
    /// 0 = Knight, 1 = Archer, 2 = Mage/Sorcerer, 3 = Spear.
    /// </summary>
    public enum PlayerClass
    {
        /// <summary>The class could not be read or the raw value is not a known class.</summary>
        Unknown = BotConstants.PlayerClassIds.Unknown,

        /// <summary>0 — Knight.</summary>
        Knight = BotConstants.PlayerClassIds.Knight,

        /// <summary>1 — Archer.</summary>
        Archer = BotConstants.PlayerClassIds.Archer,

        /// <summary>2 — Mage / Sorcerer.</summary>
        Mage = BotConstants.PlayerClassIds.Mage,

        /// <summary>3 — Spear.</summary>
        Spear = BotConstants.PlayerClassIds.Spear,
    }
}
