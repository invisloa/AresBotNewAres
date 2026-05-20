// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using static AresTrainerV3.Enums.EnumsList;

namespace AresTrainerV3.ItemInventory
{
	public interface IItemsOperationsGenerator
	{
		public List<int> ItemsForSaleListGenerate();
		public List<int> ItemsFromStorageListGenerate();
		public List<int> ItemsToStorageMoveListGenerate();
	}
}