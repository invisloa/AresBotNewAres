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

namespace AresTrainerV3.MoveModels.MoveToPoint.MouseToPosModel
{
	public class MouseToPosRemapper : IMouseToPosRemapper
	{
		CoordsPoint CharCenterPoint = new CoordsPoint(960, 522);
		// centerY +315 = currentPosY -12
		// centerY -315 = currentPosY +12
		// centerX -315 = currentPosX -12
		// centerX +315 = currentPosX +12
		public CoordsPoint RemapVectorToMousePos(int x, int y)
		{
			CoordsPoint MousePosition = CharCenterPoint;
			if (x == 1 || x == -1) { MousePosition.X += x * 28; }
			else { MousePosition.X += x * 21; }
			if (y == 1 || y == -1) { MousePosition.Y += y * 28; }
			else { MousePosition.Y += -(y * 21); }
			return MousePosition;
		}


	}
}
