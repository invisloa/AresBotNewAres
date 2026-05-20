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
using AresTrainerV3.ItemInventory.Buyer;



namespace AresTrainerV3.ItemInventory.Buyer
{
    internal class BuyerPotionHolinaExp : BuyerPotionsAbstract
    {
        public override void BuyPotions()
        {
            BuyPotionsAbstract(120, true, 80, 10, ExpBotMovePositionsValues.mousePositionsForHolinaBuying);
        }

    }
}
