using System;
using System.Diagnostics;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    public class GameMemoryService
    {
        public delegate bool ReadMemoryDelegate(uint pid, ulong address, byte[] buffer, out uint bytesRead);
        public delegate bool WriteMemoryDelegate(uint pid, ulong address, byte[] buffer, out uint bytesWritten);

        private readonly uint _pid;
        private readonly ReadMemoryDelegate _read;
        private readonly WriteMemoryDelegate _write;
        private readonly ulong _moduleBase;
        private readonly int _pointerSize;
        private readonly Action<string> _log;

        // --- Offsets from MovementSystem ---
        private const ulong PlayerPtrOffset = 0x471C88;
        private const ulong MobSelectedPtrOffset2 = 0x3F4D4C;
        private const ulong MobSelectedSubOffset2 = 0x9D;
        private const ulong MobSelectedOffset2 = 0x60;
        private const ulong XOffset = 0x144;
        private const ulong YOffset = 0xEE8;
        private const ulong HpOffset = 0x168;
        private const ulong MaxHpOffset = 0x16C;
        private const ulong ManaOffset = 0xC58;
        private const ulong MaxMpOffset = 0xC5C;
        private const ulong IsInCityOffset = 0x5F4;
        private const ulong MapNumberOffset = 0x5F0;
        private const ulong CurrentMapOffset = 0x5F8;
        private const ulong CurrentWeightOffset = 0xC70;
        private const ulong MaxWeightOffset = 0xC74;
        private const ulong RunSpeedOffset = 0x150A;
        private const ulong SkillSpeedOffset = 0xACA;
        private const ulong Animation1Offset = 0x3ba;
        private const ulong Animation2Offset = 0x3be;
        private const ulong InventorySlotHPCountOffset = 0xF20;
        private const ulong ManaPotionsCountOffset = 0xF40;
        private const ulong WhitePotionsCountOffset = 0xF60;
        private const ulong RedPotionsCountOffset = 0xF80;
        private const ulong InventoryFirstSlotSellValueOffset = 0x191A;
        private const ulong TargetSelectedOffset = 0x60;
        private const ulong AttackSpeed1Offset = 0x47A;
        private const ulong AttackSpeed2Offset = 0x47E;
        private const ulong CurrentActionOffset = 0x3B0;
        // A_CurrentAction values: 0=idle, 3=running, 4=being hit, 7=attacking -knight no weapon
        // A_CurrentAction values: 25=idle, 27=running, 28wd=being hit, 39=attacking -knight sword


        private const ulong CameraPtrOffset = 0x4704B0;
        private const ulong CameraDistanceOffset = 0x1a6;
        private const ulong CameraAngleOffset = 0x1aa;
        private const ulong CameraVerticalAngleOffset = 0x1be;

        // --- Offsets from RepotAddress ---
        private const ulong BaseNormalMOffset = 0x471C88;
        private const ulong UiWindowMOffset = 0x471CA8;
        private const int ShopWindow2MOffset = 0x94;
        private const int ShopWindowOffset1 = 0x100;
        private const int InventoryOpenOffset = 0xE8;

        private const int SellerWindow2MOffset = 0xac;
        private const int InventoryWindow2MOffset = 0x60;
        private const int StorageWindow2MOffset = 0x94;
        private const int ShopWindowOffset2 = 0xd8;
        private const int SellerWindowOffset1 = 0xc0;
        private const int StorageWindowOffset1 = 0xc0;
        private const int StorageWindowOffset2 = 0xd8;
        private const int InventoryWindowOffset1 = 0xc0;
        private const int InventoryWindowOffset2 = 0xd8;
        private const int InventoryCurrentTabOffset = 0x110;
        private const int InventoryCurrentTabMOffset = 0x2AD2EC;
        private const int SellWindowOffset = 0xc0;
        private const ulong SellWindowMOffset = 0x2AD308;
        private const int DeleteWindowOffset = 0x138c;
        private const int SellItemSelectedOffset = 0x12e;
        private const int SlotHPOffset = 0xbb2;
        private const int SlotMannaOffset = 0xbce;
        private const int SlotRedPotOffset = 0xbea;
        private const int SlotWhitePotOffset = 0xc06;
        private const int SlotFirstSellOffset = 0xc5a;
        private const int SlotFirstStorageValueOffset = 0x116;

        private const ulong InventoryTestPtrOffset = 0x242CB5;
        private const ulong InventoryTestSubOffset = 0xF0;

        // --- Constants from RepotAddress ---
        public const int ItemCount1 = 16777217;
        public const int MannaPotionsCountValue = 16777257;
        public const int WhitePotionsCountValue = 16777222;
        public const int RedPotionsCountValue = 16777222;
        public static readonly int[] ItemsNotForSaleValues = { 0, 246, 247, 1092, 1093, 1094, 1095, 3093 };

        // --- Offsets from LootAddress ---
        public const int SOD = -13799;
        public const int SOP = 32627;
        private const ulong CurrentItemHighlightedTypeOffset = 0x8C9Fd0;
        private const int PositionXOffset = 0x23c;

        // --- Offsets from HealManaSystem ---
        private const ulong HealManaBasePtrAddr2 = 0x8A3DA8;
        private const ulong HealManaOffset2 = 0xC58;
        private const ulong HealManaBasePtrAddr1 = 0x5C48CB;
        private const ulong HealManaOffset1 = 0x2C8;

        public GameMemoryService(uint pid, ReadMemoryDelegate read, WriteMemoryDelegate write, ulong moduleBase, int pointerSize, Action<string> log)
        {
            _pid = pid;
            _read = read;
            _write = write;
            _moduleBase = moduleBase;
            _pointerSize = pointerSize;
            _log = log;
        }

        #region Memory Helpers

        private ulong ReadPointer(ulong address)
        {
            byte[] buf = new byte[_pointerSize];
            if (_read(_pid, address, buf, out uint bytesRead) && bytesRead == _pointerSize)
            {
                return _pointerSize == 8 ? BitConverter.ToUInt64(buf, 0) : BitConverter.ToUInt32(buf, 0);
            }
            return 0;
        }

        private byte ReadByte(ulong address)
        {
            byte[] buf = new byte[1];
            if (_read(_pid, address, buf, out uint bytesRead) && bytesRead == 1)
            {
                return buf[0];
            }
            return 0;
        }

        private short ReadShort(ulong address)
        {
            byte[] buf = new byte[2];
            if (_read(_pid, address, buf, out uint bytesRead) && bytesRead == 2)
            {
                return BitConverter.ToInt16(buf, 0);
            }
            return 0;
        }

        private ushort ReadUShort(ulong address)
        {
            byte[] buf = new byte[2];
            if (_read(_pid, address, buf, out uint bytesRead) && bytesRead == 2)
            {
                return BitConverter.ToUInt16(buf, 0);
            }
            return 0;
        }

        private int ReadInt(ulong address)
        {
            byte[] buf = new byte[4];
            if (_read(_pid, address, buf, out uint bytesRead) && bytesRead == 4)
            {
                return BitConverter.ToInt32(buf, 0);
            }
            return 0;
        }

        private float ReadFloat(ulong address)
        {
            byte[] buf = new byte[4];
            if (_read(_pid, address, buf, out uint bytesRead) && bytesRead == 4)
            {
                return BitConverter.ToSingle(buf, 0);
            }
            return 0;
        }

        private bool WriteFloat(ulong address, float value)
        {
            byte[] buf = BitConverter.GetBytes(value);
            return _write(_pid, address, buf, out _);
        }

        private bool WriteShort(ulong address, short value)
        {
            byte[] buf = BitConverter.GetBytes(value);
            return _write(_pid, address, buf, out _);
        }

        #endregion

        #region Movement System Data

        public (float x, float y, bool success) GetPlayerPosition()
        {
            ulong ptrAddr = _moduleBase + PlayerPtrOffset;
            ulong playerBase = ReadPointer(ptrAddr);
            if (playerBase == 0) return (0, 0, false);

            short xVal = ReadShort(playerBase + XOffset);
            short yVal = ReadShort(playerBase + YOffset);

            return ((float)xVal, (float)yVal, true);
        }

        public short GetCameraAngle()
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return 0;

            return ReadShort(cameraBase + CameraAngleOffset);
        }

        public void SetCameraAngle(short angle)
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return;

            WriteShort(cameraBase + CameraAngleOffset, angle);
        }

        public void SetCameraVerticalAngle(short angle)
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return;

            WriteShort(cameraBase + CameraVerticalAngleOffset, angle);
        }

        public void SetCameraDistance(short distance)
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return;

            WriteShort(cameraBase + CameraDistanceOffset, distance);
        }

        public int GetAttackStatus()
        {
            // Using "Target selected working target" as indicator for attack readiness
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;

            return ReadInt(playerBase + TargetSelectedOffset);
        }

        public bool IsMobSelected()
        {
            ulong ptr = ReadPointer(_moduleBase + MobSelectedPtrOffset2);
            if (ptr == 0) return false;
            ulong sub = ReadPointer(ptr + MobSelectedSubOffset2);
            if (sub == 0) return false;
            return ReadInt(sub + MobSelectedOffset2) != 0;
        }

        public short GetAttackSpeed()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;

            return ReadShort(playerBase + AttackSpeed1Offset);
        }

        public short GetSkillSpeed()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;

            return ReadShort(playerBase + SkillSpeedOffset);
        }

        public (int hp, int mana, bool success) GetHpMana()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return (0, 0, false);

            int hp = ReadShort(playerBase + HpOffset);
            int mana = ReadShort(playerBase + ManaOffset);
            return (hp, mana, true);
        }

        public (int maxHp, int maxMp, bool success) GetMaxHpMana()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return (0, 0, false);

            int maxHp = ReadShort(playerBase + MaxHpOffset);
            int maxMp = ReadShort(playerBase + MaxMpOffset);
            return (maxHp, maxMp, true);
        }

        public int GetMapNumber()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadShort(playerBase + MapNumberOffset);
        }

        public int GetCurrentMap()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadInt(playerBase + CurrentMapOffset);
        }

        /// <summary>
        /// Returns current action state: 0=idle, 3=running, 4=being hit, 7=attacking
        /// </summary>
        public byte GetCurrentAction()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;

            return ReadByte(playerBase + CurrentActionOffset);
        }

        public bool GetIsInCity()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return false;
            return ReadInt(playerBase + IsInCityOffset) == 1;
        }

        public (int current, int max) GetWeight()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return (0, 0);
            return (ReadShort(playerBase + CurrentWeightOffset), ReadShort(playerBase + MaxWeightOffset));
        }

        #endregion

        #region Repot System Data

        public ulong GetBaseNormalAddress()
        {
            return ReadPointer(_moduleBase + BaseNormalMOffset);
        }

        public ulong GetUiWindowAddress()
        {
            return ReadPointer(_moduleBase + UiWindowMOffset);
        }

        public ulong GetShopWindowAddress()
        {
            ulong uiWindow = GetUiWindowAddress();
            if (uiWindow == 0) return 0;
            return ReadPointer(uiWindow + (ulong)ShopWindow2MOffset);
        }

        public bool IsShopOpen()
        {
            ulong shopWindow = GetShopWindowAddress();
            if (shopWindow == 0) return false;
            return ReadByte(shopWindow + (ulong)ShopWindowOffset1) == 1;
        }

        public bool IsInventoryOpen()
        {
            ulong uiWindow = GetUiWindowAddress();
            if (uiWindow == 0) return false;
            ulong invWindow = ReadPointer(uiWindow + (ulong)InventoryWindow2MOffset);
            if (invWindow == 0) return false;
            return ReadByte(invWindow + (ulong)InventoryOpenOffset) == 1;
        }

        public int GetManaPotionCount()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadShort(playerBase + ManaPotionsCountOffset);
        }

        public int GetRedPotionCount()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadShort(playerBase + RedPotionsCountOffset);
        }

        public int GetWhitePotionCount()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadShort(playerBase + WhitePotionsCountOffset);
        }

        public int GetHpPotionCount()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadShort(playerBase + InventorySlotHPCountOffset);
        }

        public int GetInventoryItemType(int slotIndex)
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            // First slot sell value as indicator
            if (slotIndex == 0) return ReadShort(playerBase + InventoryFirstSlotSellValueOffset);
            return 0;
        }

        public bool IsSellWindowOpen()
        {
            // Placeholder: keeping old logic or update if needed
            ulong sellWindow = ReadPointer(_moduleBase + SellWindowMOffset);
            if (sellWindow == 0) return false;
            return ReadByte(sellWindow + (ulong)SellWindowOffset) == 1;
        }

        #endregion

        #region Loot System Data

        public int GetCurrentItemHighlightedType()
        {
            ulong address = _moduleBase + CurrentItemHighlightedTypeOffset;
            return ReadShort(address);
        }

        public int GetLootPositionX()
        {
             ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
             if (playerBase == 0) return 0;

             return ReadShort(playerBase + XOffset);
        }

        #endregion

        #region HealMana System Data

        public (short? value1, short? value2) GetHealManaValues()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return (null, null);

            short hp = ReadShort(playerBase + HpOffset);
            short mana = ReadShort(playerBase + ManaOffset);

            return (hp, mana);
        }

        public int GetAnimation1()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadInt(playerBase + Animation1Offset);
        }
        #endregion

        #region Snapshot

        /// <summary>
        /// Returns a single snapshot of the player's current game state.
        /// All decision-making should use this snapshot rather than querying memory ad-hoc.
        /// </summary>
        public GameSnapshot GetSnapshot()
        {
            var (x, y, posSuccess) = GetPlayerPosition();
            int mapNumber = GetMapNumber();
            int currentMap = GetCurrentMap();
            bool isInCity = GetIsInCity();
            var (hp, mana, hpManaSuccess) = GetHpMana();
            int hpPotions = GetHpPotionCount();
            int manaPotions = GetManaPotionCount();
            var (currentWeight, maxWeight) = GetWeight();

            return new GameSnapshot(
                x: posSuccess ? x : 0f,
                y: posSuccess ? y : 0f,
                mapNumber: mapNumber,
                currentMap: currentMap,
                isInCity: isInCity,
                hp: hpManaSuccess ? hp : 0,
                mana: hpManaSuccess ? mana : 0,
                hpPotions: hpPotions,
                manaPotions: manaPotions,
                currentWeight: currentWeight,
                maxWeight: maxWeight
            );
        }

        #endregion
    }
}
