// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AresTrainerV3.HealBot.Repoter.Returner
{
    internal class GoBackExpGoblinsTeleport : GoBackExpAbstract
    {
        public override void GoBackExp()
        {
            ProgramHandle.SetCameraForExpBot();
            if (Factory.WhichBotThreadToStart == Enums.EnumsList.MoverBotEnums.HolinaGoblins)
            {
                ProgramHandle.TeleportToPositionTuple(TeleportValues.HolinaGoblinsExp);
            }
			else if (Factory.WhichBotThreadToStart == Enums.EnumsList.MoverBotEnums.BucksLowLVL)
			{
				ProgramHandle.TeleportToPositionTuple(TeleportValues.HolinaBucksLowLVLExp);
			}
			ProgramHandle.SetCameraForExpBot();

        }
    }
}
