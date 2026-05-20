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

namespace AresTrainerV3.ItemCollect
{
    internal class CollectSellerCry : AbstractWhatToCollect
    {
        protected override bool collectItemValues()
        {
            if (ProgramHandle.getCurrentItemHighlightedType == SOD || ProgramHandle.getCurrentItemHighlightedType == jewelery ||
                ProgramHandle.getCurrentItemHighlightedType == cryBow || ProgramHandle.getCurrentItemHighlightedType == cryOrb ||
                ProgramHandle.getCurrentItemHighlightedType == cryPhasor || ProgramHandle.getCurrentItemHighlightedType == crySword
                || ProgramHandle.getCurrentItemHighlightedType == cryStaff || ProgramHandle.getCurrentItemHighlightedType == stones)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}