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

namespace AresTrainerV3.ItemInventory.Buyer
{
	public class BuyerPotionsHershalExp : BuyerPotionsAbstract
    {
        public override void BuyPotions()
        {
            if (PointersAndValues.isNostalgia == true)
            {
                base.BuyPotionsAbstract(80, false, 20, 5, ExpBotMovePositionsValues.mousePositionsForHershalBuying);
            }
            else
            {
                base.BuyPotionsAbstract(100, false, 200, 5, ExpBotMovePositionsValues.mousePositionsForHershalBuyingEOA);

            }
        }

    }
}
