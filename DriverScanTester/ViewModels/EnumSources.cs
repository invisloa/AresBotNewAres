using DriverScanTester.Services;

namespace DriverScanTester.ViewModels
{
    public static class EnumSources
    {
        public static System.Array LockValueTypes { get; } =
            System.Enum.GetValues(typeof(LockValueType));

        public static System.Array MovementPrecisions { get; } =
            System.Enum.GetValues(typeof(MovementPrecision));

        public static System.Array BotModes { get; } =
            System.Enum.GetValues(typeof(BotMode));

        public static System.Array LockPriorities { get; } =
            System.Enum.GetValues(typeof(LockPriority));

        public static System.Array LockGroups { get; } =
            System.Enum.GetValues(typeof(LockGroup));

        public static System.Array ZoneRestrictions { get; } =
            System.Enum.GetValues(typeof(ZoneRestriction));
    }
}
