// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using AresTrainerV3.MoveModels.MoveToPoint;
using AresTrainerV3.MoveModels.MoveToPoint.DestinationsCoords;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AresTrainerV3.HealBot.Repoter.Returner
{
	internal class GoBackExpHershalLowLvl : GoBackExpAbstract
	{
		public override void GoBackExp()
		{
			// TEST TEST TEST
			// TEST TEST TEST
			// TEST TEST TEST
			// TEST TEST TEST
			CoordsMoveOnly mover = new CoordsMoveOnly(DestinationsCoordinator.GoBackExpHershalOutsideCity);
			// TEST TEST TEST
			// TEST TEST TEST
			// TEST TEST TEST
			// TEST TEST TEST
			// TEST TEST TEST
			// TEST TEST TEST

			mover.MoveToDestination();
		}
	}
}
