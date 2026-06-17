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
        private const ulong PlayerPtrOffset = BotConstants.MemoryOffsets.PlayerPtr;
        private const ulong MobSelectedPtrOffset2 = BotConstants.MemoryOffsets.MobSelectedPtr2;
        private const ulong MobSelectedSubOffset2 = BotConstants.MemoryOffsets.MobSelectedSub2;
        private const ulong MobSelectedOffset2 = BotConstants.MemoryOffsets.MobSelected2;
        private const ulong XOffset = BotConstants.MemoryOffsets.X;
        private const ulong YOffset = BotConstants.MemoryOffsets.Y;
        private const ulong HpOffset = BotConstants.MemoryOffsets.Hp;
        private const ulong MaxHpOffset = BotConstants.MemoryOffsets.MaxHp;
        private const ulong ManaOffset = BotConstants.MemoryOffsets.Mana;
        private const ulong MaxMpOffset = BotConstants.MemoryOffsets.MaxMp;
        private const ulong IsInCityOffset = BotConstants.MemoryOffsets.IsInCity;
        private const ulong MapNumberOffset = BotConstants.MemoryOffsets.MapNumber;
        private const ulong CurrentMapOffset = BotConstants.MemoryOffsets.CurrentMap;
        private const ulong CurrentWeightOffset = BotConstants.MemoryOffsets.CurrentWeight;
        private const ulong MaxWeightOffset = BotConstants.MemoryOffsets.MaxWeight;
        private const ulong RunSpeedOffset = BotConstants.MemoryOffsets.RunSpeed;
        private const ulong SkillSpeedOffset = BotConstants.MemoryOffsets.SkillSpeed;
        private const ulong Animation1Offset = BotConstants.MemoryOffsets.Animation1;
        private const ulong Animation2Offset = BotConstants.MemoryOffsets.Animation2;
        private const ulong InventorySlotHPCountOffset = BotConstants.MemoryOffsets.InventorySlotHPCount;
        private const ulong ManaPotionsCountOffset = BotConstants.MemoryOffsets.ManaPotionsCount;
        private const ulong WhitePotionsCountOffset = BotConstants.MemoryOffsets.WhitePotionsCount;
        private const ulong RedPotionsCountOffset = BotConstants.MemoryOffsets.RedPotionsCount;
        private const ulong InventoryFirstSlotSellValueOffset = BotConstants.MemoryOffsets.InventoryFirstSlotSellValue;
        private const ulong TargetSelectedOffset = BotConstants.MemoryOffsets.TargetSelected;
        private const ulong AttackSpeed1Offset = BotConstants.MemoryOffsets.AttackSpeed1;
        private const ulong AttackSpeed2Offset = BotConstants.MemoryOffsets.AttackSpeed2;
        private const ulong CurrentActionOffset = BotConstants.MemoryOffsets.CurrentAction;
        // A_CurrentAction values: 0=idle, 3=running, 4=being hit, 7=attacking -knight no weapon
        // A_CurrentAction values: 25=idle, 27=running, 28wd=being hit, 39=attacking -knight sword


        private const ulong CameraPtrOffset = BotConstants.MemoryOffsets.CameraPtr;
        private const ulong CameraDistanceOffset = BotConstants.MemoryOffsets.CameraDistance;
        private const ulong CameraAngleOffset = BotConstants.MemoryOffsets.CameraAngle;
        private const ulong CameraVerticalAngleOffset = BotConstants.MemoryOffsets.CameraVerticalAngle;

        /// <summary>
        /// Horizontal camera yaw offset — full 32-bit float.
        /// Reads/writes must treat this as a 4-byte value; reading only the
        /// upper 2 bytes (i.e. <c>+0x1AA</c>) yields a corrupt, partial value.
        /// </summary>
        private const int CameraAngleOffsetLocal = (int)BotConstants.MemoryOffsets.CameraAngle;

        // --- Offsets from RepotAddress ---
        private const ulong BaseNormalMOffset = BotConstants.MemoryOffsets.BaseNormalM;
        private const ulong UiWindowMOffset = BotConstants.MemoryOffsets.UiWindowM;
        private const int ShopWindow2MOffset = BotConstants.MemoryOffsets.ShopWindow2M;
        private const int ShopWindowOffset1 = BotConstants.MemoryOffsets.ShopWindow1;
        private const int InventoryOpenOffset = BotConstants.MemoryOffsets.InventoryOpen;

        private const int SellerWindow2MOffset = BotConstants.MemoryOffsets.SellerWindow2M;
        private const int InventoryWindow2MOffset = BotConstants.MemoryOffsets.InventoryWindow2M;
        private const int StorageWindow2MOffset = BotConstants.MemoryOffsets.StorageWindow2M;
        private const int ShopWindowOffset2 = BotConstants.MemoryOffsets.ShopWindow2;
        private const int SellerWindowOffset1 = BotConstants.MemoryOffsets.SellerWindow1;
        private const int StorageWindowOffset1 = BotConstants.MemoryOffsets.StorageWindow1;
        private const int StorageWindowOffset2 = BotConstants.MemoryOffsets.StorageWindow2;
        private const int InventoryWindowOffset1 = BotConstants.MemoryOffsets.InventoryWindow1;
        private const int InventoryWindowOffset2 = BotConstants.MemoryOffsets.InventoryWindow2;
        private const int InventoryCurrentTabOffset = BotConstants.MemoryOffsets.InventoryCurrentTab;
        private const int InventoryCurrentTabMOffset = BotConstants.MemoryOffsets.InventoryCurrentTabM;
        private const int DeleteWindowOffset = BotConstants.MemoryOffsets.DeleteWindow;
        private const int SellItemSelectedOffset = BotConstants.MemoryOffsets.SellItemSelected;
        private const int SlotHPOffset = BotConstants.MemoryOffsets.SlotHP;
        private const int SlotMannaOffset = BotConstants.MemoryOffsets.SlotManna;
        private const int SlotRedPotOffset = BotConstants.MemoryOffsets.SlotRedPot;
        private const int SlotWhitePotOffset = BotConstants.MemoryOffsets.SlotWhitePot;
        private const int SlotFirstSellOffset = BotConstants.MemoryOffsets.SlotFirstSell;
        private const int SlotFirstStorageValueOffset = BotConstants.MemoryOffsets.SlotFirstStorageValue;

        private const ulong InventoryTestPtrOffset = BotConstants.MemoryOffsets.InventoryTestPtr;
        private const ulong InventoryTestSubOffset = BotConstants.MemoryOffsets.InventoryTestSub;

        // --- Constants from RepotAddress ---
        public const int ItemCount1 = BotConstants.GameMagicValues.ItemCount1;
        public const int MannaPotionsCountValue = BotConstants.GameMagicValues.MannaPotionsCountValue;
        public const int WhitePotionsCountValue = BotConstants.GameMagicValues.WhitePotionsCountValue;
        public const int RedPotionsCountValue = BotConstants.GameMagicValues.RedPotionsCountValue;
        public static readonly int[] ItemsNotForSaleValues = BotConstants.GameMagicValues.ItemsNotForSale;

        // --- Offsets from LootAddress ---
        public const int SOD = BotConstants.GameMagicValues.Sod;
        public const int SOP = BotConstants.GameMagicValues.Sop;
        private const ulong CurrentItemHighlightedTypeOffset = BotConstants.MemoryOffsets.CurrentItemHighlightedType;
        private const int PositionXOffset = BotConstants.MemoryOffsets.PositionX;

        // --- Offsets from HealManaSystem ---
        private const ulong HealManaBasePtrAddr2 = BotConstants.MemoryOffsets.HealManaBasePtr2;
        private const ulong HealManaOffset2 = BotConstants.MemoryOffsets.HealManaOffset2;
        private const ulong HealManaBasePtrAddr1 = BotConstants.MemoryOffsets.HealManaBasePtr1;
        private const ulong HealManaOffset1 = BotConstants.MemoryOffsets.HealManaOffset1;

        // --- Offsets for NPC mouseover ---
        private const ulong IsNpcMousePointedPtrOffset = BotConstants.MemoryOffsets.IsNpcMousePointedPtr;
        private const ulong IsNpcMousePointedOffset = BotConstants.MemoryOffsets.IsNpcMousePointed;

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

        private bool WriteInt(ulong address, int value)
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

        /// <summary>
        /// Reads the raw camera rotation (horizontal yaw) from memory as a
        /// 32-bit float in radians. The value is normalised to the [0, 2π)
        /// range so callers always see a positive, monotonically-comparable angle.
        /// </summary>
        public float GetCameraAngle()
        {
            return NormalizeRadians(ReadCameraAngleRadians());
        }

        /// <summary>
        /// Writes the camera rotation (horizontal yaw) to memory as a
        /// 32-bit float in radians. The caller is expected to pass an angle
        /// in the [0, 2π) range, matching what <see cref="GetCameraAngle"/> returns.
        /// Internally the float is serialised to its <see cref="int"/> bit
        /// pattern and written as a 32-bit integer at <c>+0x1A8</c>, so the
        /// value round-trips without truncation.
        /// </summary>
        public void SetCameraAngle(float radians)
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return;

            int bits = BitConverter.SingleToInt32Bits(radians);
            WriteInt(cameraBase + (ulong)CameraAngleOffsetLocal, bits);
        }

        // ────────────────────────────────────────────────────────────
        //  CENTRAL CAMERA-ANGLE HELPER
        //  All camera-angle reads/writes should go through this set of
        //  methods so the address, byte-width, and serialisation format
        //  are kept in one place. The legacy approach of reading the upper
        //  2 bytes at +0x1AA or interpreting partial-byte values like
        //  16585/16635 as a real angle is incorrect — those are fragments
        //  of the 32-bit float at +0x1A8.
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Cardinal directions that have a pre-computed exact bit-pattern
        /// stored under <see cref="BotConstants.CameraAngleBits"/>. Use
        /// <see cref="SetCameraAngle(CameraCardinalAngle)"/> to set the
        /// camera to one of these exact angles without any rounding error.
        /// </summary>
        public enum CameraCardinalAngle
        {
            North0,
            East90,
            South180,
            Deg240,
            West270,
            North360
        }

        /// <summary>
        /// Resolves a <see cref="CameraCardinalAngle"/> to its exact
        /// 32-bit float bit-pattern (as <see cref="int"/>). These values
        /// come from a measured read of the 4-byte float at +0x1A8.
        /// </summary>
        public static int GetCameraAngleInt(CameraCardinalAngle angle)
        {
            return angle switch
            {
                CameraCardinalAngle.North0   => BotConstants.CameraAngleBits.North_0Deg,
                CameraCardinalAngle.East90   => BotConstants.CameraAngleBits.East_90Deg,
                CameraCardinalAngle.South180 => BotConstants.CameraAngleBits.South_180Deg,
                CameraCardinalAngle.West270  => BotConstants.CameraAngleBits.West_270Deg,
                CameraCardinalAngle.North360 => BotConstants.CameraAngleBits.North_360Deg,
                _ => BotConstants.CameraAngleBits.North_0Deg
            };
        }

        /// <summary>
        /// Reads the camera horizontal yaw as the raw 4-byte float stored
        /// at <c>+0x1A8</c>. The returned value is in radians and is NOT
        /// normalised — multiple full rotations will accumulate.
        /// </summary>
        public float ReadCameraAngleRadians()
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return 0f;
            return ReadFloat(cameraBase + (ulong)CameraAngleOffsetLocal);
        }

        /// <summary>
        /// Reads the camera horizontal yaw and returns it as a normalised
        /// degree value in <c>[0, 360)</c>.
        /// </summary>
        public float ReadCameraAngleDegreesNormalized()
        {
            float radians = ReadCameraAngleRadians();
            float degrees = RadiansToDegrees(radians);
            return NormalizeDegrees(degrees);
        }

        /// <summary>
        /// Writes the exact 32-bit float bit-pattern (as <see cref="int"/>)
        /// to <c>+0x1A8</c>. Use this when the bit-pattern is already known,
        /// e.g. from <see cref="GetCameraAngleInt(CameraCardinalAngle)"/>.
        /// </summary>
        public void WriteCameraAngleInt(int angleIntValue)
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return;
            WriteInt(cameraBase + (ulong)CameraAngleOffsetLocal, angleIntValue);
        }

        /// <summary>
        /// Sets the camera horizontal yaw to a known cardinal direction by
        /// writing its pre-computed exact bit-pattern. This bypasses any
        /// float-to-int conversion rounding, so the result is exact.
        /// </summary>
        public void SetCameraAngle(CameraCardinalAngle angle)
        {
            WriteCameraAngleInt(GetCameraAngleInt(angle));
        }

        /// <summary>
        /// Emits a one-line diagnostic log of the camera angle: raw 32-bit
        /// integer at <c>+0x1A8</c>, hex representation, radians, degrees,
        /// and degrees normalised to <c>[0, 360)</c>.
        /// </summary>
        public void LogCameraAngleDebug()
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0)
            {
                _log?.Invoke($"[CameraAngle] cameraBase=0 (pointer chain failed).");
                return;
            }

            int raw = ReadInt(cameraBase + (ulong)CameraAngleOffsetLocal);
            float radians = BitConverter.Int32BitsToSingle(raw);
            float degrees = RadiansToDegrees(radians);
            float normalized = NormalizeDegrees(degrees);

            _log?.Invoke($"[CameraAngle] rawInt={raw}, hex=0x{raw:X8}, radians={radians:F6}, degrees={degrees:F3}, normalized={normalized:F3}");
        }

        // ── Unit-conversion helpers used by the camera-angle API ──

        /// <summary>Wraps an angle in degrees into the <c>[0, 360)</c> range.</summary>
        public static float NormalizeDegrees(float degrees)
        {
            degrees %= 360.0f;
            if (degrees < 0.0f) degrees += 360.0f;
            return degrees;
        }

        /// <summary>Wraps an angle in radians into the <c>[0, 2π)</c> range.</summary>
        public static float NormalizeRadians(float radians)
        {
            float twoPi = (float)(2.0 * Math.PI);
            if (float.IsNaN(radians) || float.IsInfinity(radians)) return 0f;
            float normalized = radians % twoPi;
            if (normalized < 0.0f) normalized += twoPi;
            return normalized;
        }

        /// <summary>Converts radians to degrees.</summary>
        public static float RadiansToDegrees(float radians) => radians * 180.0f / (float)Math.PI;

        /// <summary>Converts degrees to radians.</summary>
        public static float DegreesToRadians(float degrees) => degrees * (float)Math.PI / 180.0f;

        /// <summary>Reinterprets a 32-bit bit-pattern as a <see cref="float"/>.</summary>
        public static float IntBitsToFloat(int value) => BitConverter.Int32BitsToSingle(value);

        /// <summary>Reinterprets a <see cref="float"/> as its 32-bit bit-pattern.</summary>
        public static int FloatToIntBits(float value) => BitConverter.SingleToInt32Bits(value);

        /// <summary>
        /// Reads the raw unbounded vertical camera angle from memory (radians as 4-byte float).
        /// </summary>
        private float ReadRawCameraVerticalRadians()
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return 0;

            return ReadFloat(cameraBase + CameraVerticalAngleOffset);
        }

        /// <summary>
        /// Gets the camera vertical angle normalized to the [0, 2π) range (radians).
        /// Uses modulo (%) to prevent infinitely growing radian values from multiple full rotations.
        /// </summary>
        public float GetCameraVerticalRadians()
        {
            float raw = ReadRawCameraVerticalRadians();
            float twoPi = (float)(2 * Math.PI);
            float normalized = raw % twoPi;
            if (normalized < 0) normalized += twoPi;
            return normalized;
        }

        /// <summary>
        /// Gets the camera vertical angle in degrees (0–360), derived from the normalized radians.
        /// </summary>
        public float CameraVerticalDegrees
        {
            get { return GetCameraVerticalRadians() * (180f / (float)Math.PI); }
        }

        /// <summary>
        /// Sets the camera vertical angle from a radian float value.
        /// </summary>
        public void SetCameraVerticalAngle(float radians)
        {
            ulong ptrAddr = _moduleBase + CameraPtrOffset;
            ulong cameraBase = ReadPointer(ptrAddr);
            if (cameraBase == 0) return;

            WriteFloat(cameraBase + CameraVerticalAngleOffset, radians);
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
            int targetId = ReadInt(sub + MobSelectedOffset2);
            // targetId > MaxMobTargetId indicates a player character — skip it
            return targetId != 0 && targetId < BotConstants.Combat.MaxMobTargetId;
        }

        /// <summary>
        /// Returns true only when a player character (not a mob/NPC) is confirmed as the current target.
        /// Unlike <see cref="IsMobSelected()"/>, this does NOT return true when the pointer chain
        /// fails or targetId is 0 — it specifically checks for targetId > <see cref="BotConstants.Combat.MaxMobTargetId"/>.
        /// </summary>
        public bool IsPlayerSelected()
        {
            ulong ptr = ReadPointer(_moduleBase + MobSelectedPtrOffset2);
            if (ptr == 0) return false;
            ulong sub = ReadPointer(ptr + MobSelectedSubOffset2);
            if (sub == 0) return false;
            int targetId = ReadInt(sub + MobSelectedOffset2);
            // Only return true when targetId is confirmed to be a player character (above threshold)
            return targetId > BotConstants.Combat.MaxMobTargetId;
        }

        /// <summary>
        /// Checks whether the mouse is currently pointing at an NPC/mob (highlighted).
        /// Pointer: [Ares.exe + 0x471C84] + 0x7C
        /// Returns 1 if pointed, 0 if not.
        /// </summary>
        public bool IsNpcMousePointed()
        {
            ulong ptrAddr = _moduleBase + IsNpcMousePointedPtrOffset;
            ulong baseAddr = ReadPointer(ptrAddr);
            if (baseAddr == 0) return false;
            return ReadByte(baseAddr + IsNpcMousePointedOffset) == 1;
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

        /// <summary>
        /// Reads the inventory item type (2 bytes, short) for a given slot index (0-based).
        /// ORIGINAL implementation — reads from playerBase + 0x191A (slot 0 only).
        /// </summary>
        public int GetInventoryItemType(int slotIndex)
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            // NOTE: Original only supported slot 0 via offset 0x191A.
            // Full slot array reading uses offset 0xc5a from old bot (see TryGetSellSlotItemType).
            if (slotIndex == 0) return ReadShort(playerBase + InventoryFirstSlotSellValueOffset);
            return 0;
        }

        // ════════════════════════════════════════════════════════════════
        //  OLD-BOT INVENTORY SLOT READING (UNVERIFIED OFFSETS)
        //  The old bot (AresTrainerV3) stored inventory slot data at
        //  playerBase + 0xC5A, with each slot = 0x1C bytes.
        //  These offsets have NOT been verified with the new game version.
        //  If they return 0 or garbage, the sell logic will simply skip items.
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets the base address for inventory sell slots.
        /// Slot 0 item type is at [Ares.exe + 0x471C88] + 0xF1A (playerBase + 0xF1A).
        /// Each slot is 0x20 bytes. Slot 1 item type is at playerBase + 0x10BA.
        /// </summary>
        private ulong TryGetInventorySlotBase()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return playerBase + (ulong)BotConstants.MemoryOffsets.SlotFirstSell; // 0xF1A
        }

        /// <summary>
        /// Reads inventory slot item type (2 bytes, short).
        /// Item type is at offset 0 of each 0x20-byte slot.
        /// Slot 0: [Ares.exe + 0x471C88] + 0xF1A
        /// Slot 1: [Ares.exe + 0x471C88] + 0x10BA
        /// </summary>
        public int TryGetSellSlotItemType(int slotIndex)
        {
            ulong slotBase = TryGetInventorySlotBase();
            if (slotBase == 0) return 0;
            ulong addr = slotBase + (ulong)(slotIndex * BotConstants.GameMagicValues.InventorySlotSize);
            return ReadShort(addr);
        }

        /// <summary>
        /// Reads inventory slot item count.
        /// Verified: [Ares.exe + 0x471C88] + 0x10A0 → slot offset +6.
        /// </summary>
        public int TryGetSellSlotItemCount(int slotIndex)
        {
            ulong slotBase = TryGetInventorySlotBase();
            if (slotBase == 0) return 0;
            ulong addr = slotBase + (ulong)(slotIndex * BotConstants.GameMagicValues.InventorySlotSize) + 6;
            return ReadByte(addr);
        }

        /// <summary>
        /// Reads inventory slot item stat1 (Baruch).
        /// Verified offset: Baruch = [Ares.exe + 0x471C88] + 0x109C → slot offset +2.
        /// </summary>
        public int TryGetSellSlotItemStat1(int slotIndex)
        {
            ulong slotBase = TryGetInventorySlotBase();
            if (slotBase == 0) return 0;
            ulong addr = slotBase + (ulong)(slotIndex * BotConstants.GameMagicValues.InventorySlotSize) + 2;
            return ReadByte(addr);
        }

        /// <summary>
        /// Reads inventory slot item stat2 (Keluchi).
        /// Verified: [Ares.exe + 0x471C88] + 0x109E → slot offset +4.
        /// </summary>
        public int TryGetSellSlotItemStat2(int slotIndex)
        {
            ulong slotBase = TryGetInventorySlotBase();
            if (slotBase == 0) return 0;
            ulong addr = slotBase + (ulong)(slotIndex * BotConstants.GameMagicValues.InventorySlotSize) + 4;
            return ReadByte(addr);
        }

        /// <summary>
        /// Reads storage item type using old-bot offsets (UI chain + 0x116).
        /// UNVERIFIED.
        /// </summary>
        public int TryGetStorageItemType(int slotIndex)
        {
            ulong storageSlotBase = TryGetStorageSlotBase();
            if (storageSlotBase == 0) return 0;
            ulong addr = storageSlotBase + (ulong)(slotIndex * BotConstants.GameMagicValues.InventorySlotSize) - 4;
            return ReadShort(addr);
        }

        /// <summary>
        /// Reads storage item count using old-bot offsets.
        /// UNVERIFIED.
        /// </summary>
        public int TryGetStorageItemCount(int slotIndex)
        {
            ulong storageSlotBase = TryGetStorageSlotBase();
            if (storageSlotBase == 0) return 0;
            ulong addr = storageSlotBase + (ulong)(slotIndex * BotConstants.GameMagicValues.InventorySlotSize);
            return ReadByte(addr);
        }

        /// <summary>
        /// Reads storage item stat1 using old-bot offsets.
        /// UNVERIFIED.
        /// </summary>
        public int TryGetStorageItemStat1(int slotIndex)
        {
            ulong storageSlotBase = TryGetStorageSlotBase();
            if (storageSlotBase == 0) return 0;
            ulong addr = storageSlotBase + (ulong)(slotIndex * BotConstants.GameMagicValues.InventorySlotSize) - 2;
            return ReadByte(addr);
        }

        /// <summary>
        /// Reads storage item stat2 using old-bot offsets.
        /// UNVERIFIED.
        /// </summary>
        public int TryGetStorageItemStat2(int slotIndex)
        {
            ulong storageSlotBase = TryGetStorageSlotBase();
            if (storageSlotBase == 0) return 0;
            ulong addr = storageSlotBase + (ulong)(slotIndex * BotConstants.GameMagicValues.InventorySlotSize) - 1;
            return ReadByte(addr);
        }

        /// <summary>
        /// Gets the base address for storage slots using old-bot offsets.
        /// UNVERIFIED.
        /// </summary>
        private ulong TryGetStorageSlotBase()
        {
            ulong storageWindow = TryGetStorageWindowAddress();
            if (storageWindow == 0) return 0;
            return storageWindow + (ulong)BotConstants.MemoryOffsets.SlotFirstStorageValue;
        }

        /// <summary>
        /// Gets the storage window address via UI chain (same pattern as shop window).
        /// UNVERIFIED.
        /// </summary>
        private ulong TryGetStorageWindowAddress()
        {
            ulong uiWindow = GetUiWindowAddress();
            if (uiWindow == 0) return 0;
            return ReadPointer(uiWindow + (ulong)BotConstants.MemoryOffsets.StorageWindow2M);
        }

        /// <summary>
        /// Returns the current inventory tab (0 = first tab, 1 = second tab).
        /// Pointer chain provided by user: [[Ares.exe + 0x4CAEE8] - 0x9] - 0x36B
        /// </summary>
        public int TryGetCurrentInventoryTab()
        {
            // Read [[moduleBase + 0x4CAEE8] - 0x9]
            ulong ptr1 = ReadPointer(_moduleBase + BotConstants.MemoryOffsets.InventoryTabSelectedPtr);
            if (ptr1 == 0) return 0;
            ulong ptr2 = ReadPointer(ptr1 - (ulong)BotConstants.MemoryOffsets.InventoryTabSelectedSub1);
            if (ptr2 == 0) return 0;
            // Subtract 0x36B to get the final address, read byte
            ulong finalAddr = ptr2 - (ulong)BotConstants.MemoryOffsets.InventoryTabSelectedSub2;
            return ReadByte(finalAddr);
        }

        /// <summary>
        /// Checks whether the storage window is currently open.
        /// Uses old-bot offset pattern — UNVERIFIED.
        /// </summary>
        public bool IsStorageOpen()
        {
            ulong storageWindow = TryGetStorageWindowAddress();
            if (storageWindow == 0) return false;
            return ReadByte(storageWindow + (ulong)BotConstants.MemoryOffsets.StorageWindow1) == 1;
        }

        /// <summary>
        /// Checks whether the SELL CONFIRMATION dialog is open (popup after right-clicking an item).
        /// Pointer: [Ares.exe + 0x471C98] + 0xE8
        /// </summary>
        public bool IsSellConfirmWindowOpen()
        {
            ulong confirmWindow = ReadPointer(_moduleBase + BotConstants.MemoryOffsets.SellConfirmWindowPtr);
            if (confirmWindow == 0) return false;
            return ReadByte(confirmWindow + 0xE8) == 1;
        }

        /// <summary>
        /// Reads max weight from player structure.
        /// </summary>
        public int GetMaxWeight()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadShort(playerBase + MaxWeightOffset);
        }

        /// <summary>
        /// Reads current weight from player structure.
        /// </summary>
        public int GetCurrentWeight()
        {
            ulong playerBase = ReadPointer(_moduleBase + PlayerPtrOffset);
            if (playerBase == 0) return 0;
            return ReadShort(playerBase + CurrentWeightOffset);
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
