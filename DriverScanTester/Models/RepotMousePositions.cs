using System;

namespace DriverScanTester.Models
{
    public static class RepotMousePositions
    {
        public static (int X, int Y)[] mousePositionsForHershalBuying =
        {
            (995, 380),  //mana pot (L)
            (995, 535),  //red pot
            (995, 500),  //white pot
            (995, 225)   //hp yarrow pot
        };

        public static (int X, int Y)[] mousePositionsForHershalBuyingEOA =
        {
            (995, 310),  //mana pot (S)
            (995, 460),  //red pot
            (995, 425),  //white pot
            (995, 185),  //hp pot Hysop
        };

        public static (int X, int Y)[] mousePositionsForHolinaBuying =
        {
            (995, 340),  //mana pot (S)
            (995, 535),  //red pot
            (995, 495),  //white pot
            (995, 225),  //hp pot Yarrow
        };

        public static (int X, int Y)[] mousePositionsForSacredLandsBuying =
        {
            (995, 305),  //mana pot (S)
            (995, 425),  //red pot
            (995, 385),  //white pot
            (995, 185),  //hp pot Sage
        };

        public static (int X, int Y)[] mousePositionsForKharonBuying =
        {
            (995, 305),  //mana pot
            (995, 420),  //red pot
            (995, 380),  //white pot
            (995, 185)   //hp yarrow pot
        };

        public static (int X, int Y)[] mousePositionsForStorageBuying =
        {
            (1015, 720),  //mana pot
            (1050, 720),  //red pot
            (1085, 720),  //white pot
            (985, 720)    //hp yarrow pot
        };

        public static (int X, int Y)[] HershalRepotMovePositions =
        {
            (523, 471),
            (504, 677),
            (654, 805),
            (789, 748),
        };

        public static (int X, int Y)[] HershalRepotMovePositions2 =
        {
            (1308, 173),
            (1450, 370),
            (1450, 370),
            (1443, 648),
        };

        public static (int X, int Y)[] KharonRepotMovePositions =
        {
            (1250, 170),
            (1250, 170),
            (920, 345),
        };

        public static (int X, int Y)[] UWCFirstFloorMovement =
        {
            (600, 520),
            (960, 300),
            (1250, 520),
            (960, 620),
        };

        // Inventory Grid Logic
        private static (int X, int Y)[] ItemsInvArrPosInit(int startX, int startY, int columns, bool isNotStorage)
        {
            int numberOfSpaces = isNotStorage ? 72 : 98;
            var tempTupleArr = new (int X, int Y)[numberOfSpaces];
            int spaceMultiplier = 0;
            int spaceBetweenRows = 0;

            for (int i = 0; i < numberOfSpaces; i++)
            {
                if (i == 36 && isNotStorage)
                {
                    spaceBetweenRows = 0;
                    spaceMultiplier = 0;
                }

                if (spaceMultiplier > columns)
                {
                    spaceMultiplier = 0;
                    spaceBetweenRows += 35;
                }

                tempTupleArr[i] = (startX + spaceMultiplier * 35, startY + spaceBetweenRows);
                spaceMultiplier++;
            }
            return tempTupleArr;
        }

        // Window-relative base (was absolute 1260,530 for reference window 447,77).
        // 1260 - 447 = 813, 530 - 77 = 453
        static readonly int inventoryFirstSlotX = 813;
        static readonly int inventoryFirstSlotY = 453;
        static readonly int inventoryColumns = 5;

        static readonly int storageFirstSlotX = 985;
        static readonly int storageFirstSlotY = 175;
        static readonly int storageColumns = 6;

        public static (int X, int Y)[] itemSellPositions = ItemsInvArrPosInit(inventoryFirstSlotX, inventoryFirstSlotY, inventoryColumns, true);
        public static (int X, int Y)[] itemMoveFromStoragePositions = ItemsInvArrPosInit(storageFirstSlotX, storageFirstSlotY, storageColumns, false);
    }
}
