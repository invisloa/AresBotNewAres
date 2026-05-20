// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using AresTrainerV3;
using AresTrainerV3.ItemInventory.Buyer;
using AresTrainerV3.MoveModels.MoveToPoint;
using AresTrainerV3.MoveModels.MoveToPoint.DestinationsCoords;
using System.Diagnostics.Metrics;

namespace AresTrainerV3.HealBot.Repoter
{
	internal class RepoterHolina : RepotAbstract
	{

		protected override BuyerPotionsAbstract BuyerPotionsCity
		{
			get
			{
				if (_buyerPotionsCity == null)
				{
					_buyerPotionsCity = new BuyerPotionHolinaExp();
				}
				return _buyerPotionsCity;
			}
		}
		protected override int repotCityCheck
		{
			get
			{
				_repotCityVerification = TeleportValues.Hollina;
				return _repotCityVerification;
			}
		}

		protected override void MoveToRepot()
		{
			moverToPointRepoter.MoveToDestination();
		}
	}
}


/*
protected override void MoveToRepot()
{
	Thread.Sleep(100);
	CheckIfNotRunning();
	ProgramHandle.SetCameraForExpBot();
	ProgramHandle.SetCameraLong();
	MouseOperations.MoveAndLeftClickOperation(965, 720, 200);
	Thread.Sleep(randomizer.Next(5000));
	KeyPresser.PressKey(6, 200, 200);
	Thread.Sleep(randomizer.Next(5000));
	if (ProgramHandle.isNowStandingCity())
	{
		ProgramHandle.TeleportToPositionTuple(TeleportValues.ShopHolinaPos);

	}
}
*/