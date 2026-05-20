// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using AresTrainerV3.ItemInventory.Buyer;

namespace AresTrainerV3.ItemInventory.Buyer
{
	public class BuyerFromForm : BuyerPotionsAbstract
	{
		public override void BuyPotions()
		{
			BuyPotionsAbstract(HpPotionsToBuy, BuyMaxPotions, MpPotionsToBuy, SpeedPotionsToBuy, ExpBotMovePositionsValues.ShopBuyingPositionAssigner());
		}
	}
}