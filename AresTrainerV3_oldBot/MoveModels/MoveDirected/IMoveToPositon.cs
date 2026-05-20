// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using AresTrainerV3.DoWhileMoving;
using AresTrainerV3.ItemCollect;
using AresTrainerV3.Unstuck;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AresTrainerV3.MovePositions
{
    public interface IMoveAttackCollect
    {
		public IDoWhileMoving WhatToDoWhileMoving
        {
            get ;
        }

		public void MoveAttackAndCollect();
    }
}
