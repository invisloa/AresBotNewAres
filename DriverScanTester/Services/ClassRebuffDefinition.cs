using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>Shared buff detection and cast configuration for each supported class.</summary>
    internal sealed record ClassRebuffDefinition(string Name, short ActiveValue, byte VirtualKey, byte ScanCode)
    {
        internal static ClassRebuffDefinition? ForClass(PlayerClass playerClass) => playerClass switch
        {
            PlayerClass.Mage => Mage,
            PlayerClass.Archer => Archer,
            _ => null
        };

        private static readonly ClassRebuffDefinition Mage = new(
            "rapid buff", BotConstants.Rebuff.RapidBuffActiveValue,
            BotConstants.Keyboard.Vk4, BotConstants.Keyboard.Scan4);

        private static readonly ClassRebuffDefinition Archer = new(
            "SpeedUpBuff", BotConstants.Rebuff.SpeedUpBuffActiveValue,
            BotConstants.Keyboard.Vk5, BotConstants.Keyboard.Scan5);
    }
}
