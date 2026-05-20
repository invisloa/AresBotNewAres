// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using AresTrainerV3.ItemInventory.Buyer;

namespace AresTrainerV3.HealBot.Repoter
{
	internal class RepoterSacredAlliance : RepotAbstract
	{
		protected override BuyerPotionsAbstract BuyerPotionsCity
		{
			get
			{
				if (_buyerPotionsCity == null)
				{
					_buyerPotionsCity = new BuyerFromForm();
				}
				return _buyerPotionsCity;
			}
		}
		protected override int repotCityCheck
		{
			get
			{
				_repotCityVerification = TeleportValues.AllianceSacredLand;
				return _repotCityVerification;
			}
		}

		protected override void MoveToRepot()
		{

			if (ProgramHandle.isNowStandingCity())
			{
				ProgramHandle.TeleportToPositionTuple(TeleportValues.SacredlandsAlliShop);

			}
		}
	}
}

