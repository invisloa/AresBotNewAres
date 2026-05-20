// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
namespace AresTrainerV3.ItemInventory.Buyer
{
	internal class BuyerPotionSacredLandsAlliance : BuyerPotionsAbstract
	{
		public override void BuyPotions()
		{
			BuyPotionsAbstract(50, false, 250, 25, ExpBotMovePositionsValues.mousePositionsForSacredLandsBuying);
		}


	}
}
