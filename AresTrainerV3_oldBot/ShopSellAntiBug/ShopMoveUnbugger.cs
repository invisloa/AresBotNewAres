// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using AresTrainerV3.PixelScanNPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AresTrainerV3.ShopSellAntiBug
{
	public class ShopMoveUnbugger : IUnBugShop
	{
		IActionToUnbug unbugAction = Factory.CreateUnbugActionClass();
		IFindNPC npcFinder = Factory.CreateFindNPC();
		public void UnBugShop()
		{
			ProgramHandle.SetCameraForExpBot();
			unbugAction.ActionToUnBugShop();
			npcFinder.FindNpc();
		}
	}
}
