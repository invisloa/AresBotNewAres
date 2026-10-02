using DriverScanTester;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Tests for the SOD/SOP weight-relief decision used by the pink loot scan.
    ///
    /// A SOD/SOP scroll weighs 1 lb. When the player's bag is at the weight limit
    /// the pickup would fail with the game's "too heavy" message, so before clicking
    /// a confirmed pink drop the loot system drinks the potion type the player has
    /// MORE of (HP key 1 / mana key 2) — one potion is enough for one scroll. The
    /// last potion of each type is never drunk because it is the inventory-slot
    /// reserve; when both stacks are at the reserve no relief is possible.
    /// </summary>
    public sealed class LootWeightReliefTests
    {
        [Fact]
        public void ScrollWeighsOnePound()
        {
            Assert.Equal(1, BotConstants.Loot.ScrollWeightPounds);
        }

        [Theory]
        // current + 1 > max → no room for the scroll.
        [InlineData(100, 100, true)]
        [InlineData(101, 100, true)]
        [InlineData(50, 50, true)]
        // Exactly one pound of room (or more) → the scroll fits.
        [InlineData(99, 100, false)]
        [InlineData(49, 50, false)]
        [InlineData(0, 50, false)]
        // Unreadable weight (max <= 0) never requests relief.
        [InlineData(0, 0, false)]
        [InlineData(50, 0, false)]
        [InlineData(50, -1, false)]
        public void NeedsWeightReliefForScroll_OnlyWhenScrollDoesNotFit(
            int currentWeight, int maxWeight, bool expected)
        {
            Assert.Equal(expected, LootSystem.NeedsWeightReliefForScroll(currentWeight, maxWeight));
        }

        // WeightReliefPotion: None = 0, Hp = 1, Mana = 2.
        [Theory]
        // Drink the type the player has more of.
        [InlineData(10, 5, 1)]
        [InlineData(5, 10, 2)]
        // Tie → HP.
        [InlineData(5, 5, 1)]
        // The majority stack is at the slot reserve → use the other type.
        [InlineData(1, 10, 2)]
        [InlineData(10, 1, 1)]
        [InlineData(0, 10, 2)]
        // One above the reserve is still drinkable.
        [InlineData(2, 1, 1)]
        [InlineData(1, 2, 2)]
        // Both stacks at the reserve → nothing can be drunk.
        [InlineData(1, 1, 0)]
        [InlineData(1, 0, 0)]
        [InlineData(0, 1, 0)]
        [InlineData(0, 0, 0)]
        public void ChooseWeightReliefPotion_PicksMajorityAndKeepsSlotReserve(
            int hpPotions, int manaPotions, int expected)
        {
            Assert.Equal(
                (LootSystem.WeightReliefPotion)expected,
                LootSystem.ChooseWeightReliefPotion(hpPotions, manaPotions));
        }
    }
}
