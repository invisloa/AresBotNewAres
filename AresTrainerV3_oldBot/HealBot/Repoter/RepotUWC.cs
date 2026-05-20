// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using AresTrainerV3.ItemInventory.Buyer;

namespace AresTrainerV3.HealBot.Repoter
{
	internal class RepotUWC : RepotAbstract
	{

		protected override BuyerPotionsAbstract BuyerPotionsCity
		{
			get
			{
				if (_buyerPotionsCity == null)
				{
					_buyerPotionsCity = new BuyerPotionsHershalExp();
				}
				return _buyerPotionsCity;
			}
		}
		protected override int repotCityCheck
		{
			get
			{
				_repotCityVerification = TeleportValues.Hershal;
				return _repotCityVerification;
			}
		}

		protected override void MoveToRepot()
		{

			if (ProgramHandle.isNowStandingCity())
			{
				for (int i = 0; i < 20; i++)
				{
					if (ProgramHandle.GetCurrentMap == TeleportValues.Hershal)
					{
						if (ProgramHandle.GetPositionX != 1142172652 && ProgramHandle.GetPositionY != 1141596108)
						{
							KeyPresser.PressKey(6, 1000, 1000);
						}
					}
				}
			}
			MoveToRepotWithPositions(ExpBotMovePositionsValues.HershalRepotMovePositions);
		}
	}
}