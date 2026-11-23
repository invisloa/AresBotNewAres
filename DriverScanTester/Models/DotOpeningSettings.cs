namespace DriverScanTester.Models
{
    /// <summary>
    /// Per-class "DoT opening" configuration used during the profile's EXP loop.
    /// Every newly acquired monster is attacked with skill 4 a configured number of
    /// times (2 or 3) before the ordinary skill-3 attack continues. Only Sorcerer
    /// (<see cref="PlayerClass.Mage"/>) and <see cref="PlayerClass.Archer"/> support
    /// this feature; every other class keeps the original skill-3-only behavior.
    /// </summary>
    public class DotOpeningSettings
    {
        public const int MinSkill4AttackCount = 2;
        public const int MaxSkill4AttackCount = 3;
        public const int DefaultSkill4AttackCount = 3;

        public const int MinAttackIntervalMs = 500;
        public const int MaxAttackIntervalMs = 3000;
        public const int DefaultAttackIntervalMs = 1000;

        /// <summary>When false the class keeps the original skill-3-only combat behavior.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Number of skill-4 attack activations in the opening sequence (2 or 3).</summary>
        public int Skill4AttackCount { get; set; } = DefaultSkill4AttackCount;

        /// <summary>
        /// Minimum delay in milliseconds between two skill-4 attack activations.
        /// This is not an attack confirmation: the memory API exposes no per-hit signal,
        /// so the count represents initiated skill activations.
        /// </summary>
        public int AttackIntervalMs { get; set; } = DefaultAttackIntervalMs;

        /// <summary>True when the values are inside the ranges accepted by profile validation.</summary>
        public bool IsValid =>
            Skill4AttackCount is >= MinSkill4AttackCount and <= MaxSkill4AttackCount &&
            AttackIntervalMs is >= MinAttackIntervalMs and <= MaxAttackIntervalMs;
    }
}
