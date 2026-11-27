using System.Reflection;
using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.ViewModels;
using Xunit;
using MemoryFixture = DriverScanTester.Tests.BuffSlotReadTests.MemoryFixture;

namespace DriverScanTester.Tests
{
    public sealed class AutoBuffTests : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ulong Player = 0x21010000;
        private const ulong ModuleBase = 0x400000;
        private const PlayerClass Class = PlayerClass.Mage;
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("AutoBuffTests-");
        private readonly MemoryFixture _memory = new();
        private readonly List<string> _log = new();
        private readonly BuffCalibrationStore _store;
        private readonly BotProfile _profile;

        public AutoBuffTests()
        {
            _store = new BuffCalibrationStore(Path.Combine(_directory.FullName, "BuffCalibrations"));
            _profile = new BotProfile
            {
                Name = Path.Combine(_directory.FullName, "TestProfile"),
                FlowSteps = new() { new BotFlowStep() }
            };
            Assert.True(new BotProfileLoader(_log.Add).SaveProfile(_profile));

            _memory.Set(ModuleBase + BotConstants.MemoryOffsets.PlayerPtr, BitConverter.GetBytes((uint)Player));
            SetPlayerInt(BotConstants.MemoryOffsets.IsInCity, 0);
            SetPlayerInt(BotConstants.MemoryOffsets.CurrentMap, 45);
            SetPlayerInt(BotConstants.MemoryOffsets.CurrentClassPlayer, (int)Class);
            SetPlayerInt(BotConstants.MemoryOffsets.TargetSelected, 0);
            SetPlayerShort(BotConstants.MemoryOffsets.Hp, 750);
            SetPlayerShort(BotConstants.MemoryOffsets.RedPotionsCount, 7);
            SetPlayerShort(BotConstants.MemoryOffsets.WhitePotionsCount, 9);
            SetSlots();
        }

        [Fact]
        public void ProfileRoundTripPreservesMultipleBuffsAndIndependentClasses()
        {
            var (first, second) = (Add('4'), Add('5'));
            var archer = Add('4', PlayerClass.Archer);
            archer.Enabled = false;
            var loader = new BotProfileLoader(_log.Add);
            Assert.True(loader.SaveProfile(_profile));
            var loaded = Assert.IsType<BotProfile>(loader.LoadProfile(_profile.Name));

            var mage = loaded.GetAutoBuffSettings(Class);
            Assert.True(mage.Enabled);
            Assert.Equal(new[] { first.Id, second.Id }, mage.Buffs.Select(buff => buff.Id));
            Assert.Equal(new[] { (int)'4', '5' }, mage.Buffs.Select(buff => buff.VirtualKey));
            var archerBuff = Assert.Single(loaded.GetAutoBuffSettings(PlayerClass.Archer).Buffs);
            Assert.Equal(archer.Id, archerBuff.Id);
            Assert.False(archerBuff.Enabled);
            Assert.False(loaded.GetAutoBuffSettings(PlayerClass.Spear).Enabled);
            Assert.Equal(2, loaded.AutoBuffByClass.Count);
        }

        [Fact]
        public async Task OldProfilesAndDisabledSettingsDoNotStartMonitoring()
        {
            string path = Path.Combine(_directory.FullName, "Old.json");
            File.WriteAllText(path, """{"Name":"Old","FlowSteps":[{}]}""");
            var profile = Assert.IsType<BotProfile>(new BotProfileLoader(_log.Add).LoadProfile(path));
            Assert.False(profile.GetAutoBuffSettings(Class).Enabled);
            Assert.Empty(profile.AutoBuffByClass);
            Assert.Null(AutoBuffController.Create(profile, _log.Add, _store));

            Add('4');
            _profile.GetAutoBuffSettings(Class).Enabled = false;
            var rig = new Rig(_memory.CreateService(), AutoBuffController.Create(_profile, _log.Add, _store), _log.Add);
            _memory.ReadRequests.Clear();
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Empty(_memory.ReadRequests);
            Assert.Empty(rig.Keys);
        }

        [Theory]
        [InlineData(0x31)]
        [InlineData(0x32)]
        [InlineData(0x33)]
        [InlineData(0x36)]
        [InlineData(0x37)]
        [InlineData(0x38)]
        [InlineData(0x41)]
        public void ReservedAndUnsupportedKeysAreRejected(int key)
        {
            Add(key);
            Assert.Contains(ValidationErrors(), error => error.Contains("unavailable"));
        }

        [Fact]
        public void ClassKeyValidationChecksDotTeleportDuplicatesAndCapacity()
        {
            Add('4', PlayerClass.Archer);
            Add('4');
            _profile.GetOrCreateDotOpeningSettings(Class).Enabled = true;
            _profile.TeleportKey = 0x7B;
            Add(0x7B);
            var duplicate = Add('5');
            Add('5');
            Assert.Contains(ValidationErrors(), error => error.StartsWith("Mage") && error.Contains("key 4"));
            Assert.DoesNotContain(ValidationErrors(), error => error.StartsWith("Archer"));
            Assert.Contains(ValidationErrors(), error => error.Contains("key F12"));
            Assert.Contains(ValidationErrors(), error => error.Contains("more than one enabled buff"));
            duplicate.Enabled = false;
            Assert.DoesNotContain(ValidationErrors(), error => error.Contains("more than one enabled buff"));

            for (int key = 0x70; key <= 0x74; key++)
                Add(key);
            Assert.Contains(ValidationErrors(), error => error.Contains("at most 6"));
        }

        [Fact]
        public void EditorMaintainsSeparateClassRowsAndAvailableKeyChoices()
        {
            var editor = new ProfileEditorViewModel { CurrentProfile = _profile };
            editor.SelectedAutoBuffClass = PlayerClass.Knight;
            editor.AutoBuffEnabled = true;
            editor.AddAutoBuffCommand.Execute(null);
            var knight = Assert.Single(editor.AutoBuffs);

            editor.SelectedAutoBuffClass = Class;
            Assert.False(editor.AutoBuffEnabled);
            Assert.Empty(editor.AutoBuffs);
            editor.SelectedDotOpeningClass = Class;
            editor.DotOpeningEnabled = true;
            editor.TeleportKey = 0x7B;
            Assert.DoesNotContain(editor.AutoBuffKeyOptions, option => option.Key is 0x34 or 0x7B);
            Assert.Contains(editor.AutoBuffKeyOptions, option => option.Key == 0x70 && option.Value == "F1");
            editor.AddAutoBuffCommand.Execute(null);
            Assert.Single(editor.AutoBuffs).RemoveCommand.Execute(null);
            Assert.Empty(editor.AutoBuffs);

            editor.SelectedAutoBuffClass = PlayerClass.Knight;
            Assert.True(editor.AutoBuffEnabled);
            Assert.Contains(editor.AutoBuffKeyOptions, option => option.Key == 0x34);
            Assert.Equal(knight.VirtualKey, Assert.Single(editor.AutoBuffs).VirtualKey);
        }

        [Theory]
        [InlineData(PlayerClass.Knight)]
        [InlineData(PlayerClass.Archer)]
        [InlineData(PlayerClass.Mage)]
        [InlineData(PlayerClass.Spear)]
        public async Task UsesLiveClassAndTheWorkflowCopyOfTheConfiguration(PlayerClass playerClass)
        {
            foreach (PlayerClass configured in new[] { PlayerClass.Knight, PlayerClass.Archer, PlayerClass.Mage, PlayerClass.Spear })
                Add(0x70 + (int)configured, configured);

            var entry = Assert.Single(_profile.GetAutoBuffSettings(playerClass).Buffs);
            int originalKey = entry.VirtualKey;
            Guid originalId = entry.Id;
            var controller = CreateController();
            entry.Enabled = false;
            entry.VirtualKey = '5';
            entry.Id = Guid.NewGuid();
            SetPlayerInt(BotConstants.MemoryOffsets.CurrentClassPlayer, (int)playerClass);
            var rig = CreateRig(controller);
            rig.OnCast = _ => SetSlots(1224);

            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(originalKey, Assert.Single(rig.Keys));
            Assert.Equal((short)1224, _store.Load(_profile.SavedProfilePath!, playerClass, originalId, originalKey));
            Assert.Null(_store.Load(_profile.SavedProfilePath!, playerClass, entry.Id, entry.VirtualKey));
        }

        [Theory]
        [InlineData((short)1452)]
        [InlineData(short.MinValue)]
        public async Task LearnsExactlyOneNewInt16ValueWithOnePress(short value)
        {
            var buff = Add('4');
            SetSlots(720, 810);
            var rig = CreateRig();
            rig.OnCast = _ => SetSlots(810, 720, value);

            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(new[] { (int)'4' }, rig.Keys);
            Assert.Equal(value, Load(buff));
            Assert.Equal(Start.AddMilliseconds(200), Assert.Single(rig.CastTimes));
            Assert.Equal(Start.AddMilliseconds(2300), rig.Now);
            Assert.Equal(1, rig.Delays.Count(ms => ms == 200));
            Assert.False(rig.IsAutoBuffCastActive);
        }

        [Fact]
        public async Task OnlyMoveUpdatePerformsConfiguredBuffThroughTheNormalMovementTick()
        {
            var buff = Add('4');
            var rig = CreateRig();
            rig.AllowSpeedPotionBuff = false;
            rig.OnCast = _ => SetSlots(1901);

            await rig.Update(CancellationToken.None);

            Assert.Equal(BotMode.OnlyMove, rig.CurrentMode);
            Assert.Equal((int)'4', Assert.Single(rig.Keys));
            Assert.Equal((short)1901, Load(buff));
            Assert.False(rig.IsAttackKeyHeld);
            Assert.Empty(rig.PotionKeys);
            Assert.Equal(0, rig.ForwardKeyDowns);
        }

        [Fact]
        public async Task LearnsFiveSuccessiveBuffsWithFreshSnapshotsAndOneStop()
        {
            int[] keys = ['4', '5', '9', '0', 0x70];
            var entries = keys.Select(key => Add(key)).ToArray();
            SetSlots(720);
            var rig = CreateRig();
            rig.OnCast = key => SetSlot(rig.Keys.Count, (short)(key + 1500));

            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(keys, rig.Keys);
            Assert.Equal(keys.Select(key => (short?)(key + 1500)), entries.Select(entry => Load(entry)));
            Assert.Equal(1, rig.Delays.Count(ms => ms == 200));
            Assert.Equal(Enumerable.Range(0, 5).Select(index => Start.AddMilliseconds(200 + 2100 * index)), rig.CastTimes);
            Assert.Contains(_log, line => line.Contains("Before=[720,1552,1553"));
        }

        [Fact]
        public async Task PersistedValuesAreIndependentPerProfileClassEntryAndKey()
        {
            var entry = Add('4');
            var loader = new BotProfileLoader(_log.Add);
            Assert.True(loader.SaveProfile(_profile));
            Learn(entry, -520);
            Assert.Null(_store.Load(_profile.SavedProfilePath!, PlayerClass.Archer, entry.Id, entry.VirtualKey));
            Assert.Null(_store.Load(_profile.SavedProfilePath!, Class, Guid.NewGuid(), entry.VirtualKey));
            Assert.Null(_store.Load(_profile.SavedProfilePath!, Class, entry.Id, '5'));

            _profile.Name = Path.Combine(_directory.FullName, "SaveAs");
            Assert.True(loader.SaveProfile(_profile));
            Assert.Null(Load(entry));
            var rig = CreateRig();
            rig.OnCast = _ => SetSlots(1402);
            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal((short)1402, Load(entry));

            var original = Assert.IsType<BotProfile>(loader.LoadProfile(Path.Combine(_directory.FullName, "TestProfile")));
            Assert.Equal(entry.Id, Assert.Single(original.GetAutoBuffSettings(Class).Buffs).Id);
            Assert.Equal((short)-520, _store.Load(original.SavedProfilePath!, Class, entry.Id, entry.VirtualKey));
            Assert.All(Directory.EnumerateFiles(Path.Combine(_directory.FullName, "BuffCalibrations")),
                path => Assert.EndsWith(".json", path));
        }

        [Fact]
        public async Task ReorderedSlotsAndUntrackedPotionsUseOneSharedMonitorRead()
        {
            Learn(Add('4'), 1452);
            Learn(Add('5'), -2910);
            SetSlots(720, -2910, 810, 1452);
            var rig = CreateRig();
            _memory.ReadRequests.Clear();
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));

            rig.Now = Start.AddSeconds(4);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            var reads = _memory.ReadRequests.Select(read => read.address).ToArray();
            for (int slot = 0; slot < 6; slot++)
                Assert.Equal(1, reads.Count(address => address == MemoryFixture.SlotAddress(slot)));

            SetSlots(1452, 720, 810, -2910);
            rig.Now = Start.AddSeconds(5);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Empty(rig.Keys);
            Assert.DoesNotContain(_log, line => line.Contains("Buff expired"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RebuffsMissingValuesInOneStopAndChecksEachAgain(bool bothExpire)
        {
            var first = Add('4');
            var second = Add('5');
            var third = Add('9');
            Learn(first, 1452);
            Learn(second, 2910);
            Learn(third, 3521);
            SetSlots(720, 810, 1452, bothExpire ? (short)0 : (short)3521);
            var rig = CreateRig();
            rig.OnCast = key => SetSlot(key == '5' ? 4 : 5, key == '5' ? (short)2910 : (short)3521);

            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(bothExpire ? new[] { (int)'5', '9' } : new[] { (int)'5' }, rig.Keys);
            Assert.Equal(1, rig.Delays.Count(ms => ms == 200));
            Assert.Equal((short)2910, Load(second));
            Assert.Equal((short)3521, Load(third));
        }

        [Fact]
        public async Task RebuffSkipsAnotherQueuedValueThatReappearsBeforeItsPress()
        {
            Learn(Add('4'), 1452);
            Learn(Add('5'), 2910);
            var rig = CreateRig();
            rig.OnCast = _ => SetSlots(1452, 2910);

            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(new[] { (int)'4' }, rig.Keys);
        }

        [Fact]
        public async Task RouteReplacementAfterRepotPreservesCalibrationAndAttemptState()
        {
            var successful = Add('4');
            var failed = Add('5');
            var controller = CreateController();
            var firstRoute = CreateRig(controller);
            firstRoute.OnCast = key =>
            {
                if (key == '4') SetSlots(125);
            };
            Assert.True(await firstRoute.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal((short)125, Load(successful));
            Assert.Null(Load(failed));

            // Repot clears effects and PathRunner builds a new MovementSystem.
            SetSlots();
            var followingRoute = CreateRig(controller);
            followingRoute.OnCast = _ => SetSlots(125);
            Assert.True(await followingRoute.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(new[] { (int)'4' }, followingRoute.Keys);
            Assert.Contains(_log, line => line.Contains("Rebuff successful"));

            var restart = CreateRig();
            restart.OnCast = _ => SetSlots(125, 321);
            Assert.True(await restart.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(new[] { (int)'5' }, restart.Keys);
            Assert.Equal((short)321, Load(failed));
            Assert.Equal((short)125, Load(successful));
        }

        [Fact]
        public async Task CalibrationWaitsForPotionsAndPreventsPotionInputUntilConfirmation()
        {
            var buff = Add('4');
            var rig = CreateRig();
            rig.PressSpeedPotion(GameInput.VK_7, GameInput.SCAN_7);
            rig.PressSpeedPotion(GameInput.VK_6, GameInput.SCAN_6);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            rig.Now = Start.AddMilliseconds(1999);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            rig.Now = Start.AddMilliseconds(2000);
            rig.OnCast = _ =>
            {
                Assert.True(rig.IsAutoBuffCastActive);
                rig.PressSpeedPotion(GameInput.VK_7, GameInput.SCAN_7);
                SetSlots(3342);
            };
            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(new byte[] { GameInput.VK_7, GameInput.VK_6 }, rig.PotionKeys);
            Assert.Equal((short)3342, Load(buff));
            rig.PressSpeedPotion(GameInput.VK_7, GameInput.SCAN_7);
            Assert.Equal(3, rig.PotionKeys.Count);
        }

        [Fact]
        public async Task PotionInputOnAnAuxiliaryRouteProtectsLearningOnTheNormalRoute()
        {
            var buff = Add('4');
            var attachment = _memory.CreateService();
            var recovery = new Rig(attachment, null, _log.Add);
            recovery.PressSpeedPotion(GameInput.VK_6, GameInput.SCAN_6);
            var normal = new Rig(attachment, CreateController(), _log.Add) { Now = Start.AddMilliseconds(1999) };
            normal.OnCast = _ => SetSlots(1234);

            Assert.False(await normal.TryCastAutoBuffsAsync(CancellationToken.None));
            normal.Now = Start.AddSeconds(2);
            Assert.True(await normal.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal((short)1234, Load(buff));
        }

        [Theory]
        [InlineData(BotConstants.MemoryOffsets.WhitePotionsCount)]
        [InlineData(BotConstants.MemoryOffsets.RedPotionsCount)]
        public async Task CalibrationRejectsAChangedPotionCount(ulong offset)
        {
            var buff = Add('4');
            var rig = CreateRig();
            rig.OnCast = _ =>
            {
                SetSlots(342);
                SetPlayerShort(offset, 6);
            };
            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Null(Load(buff));
            Assert.Contains(_log, line => line.Contains("speed potion counts changed"));

            rig.Now = Start.AddSeconds(5);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Single(rig.Keys);
        }

        [Theory]
        [InlineData("no new value", 0)]
        [InlineData("multiple new values", 2)]
        [InlineData("disappeared before confirmation", 1)]
        public async Task RejectsUnconfirmedLearningAndContinuesWithTheNextBuff(string reason, int newValues)
        {
            var failed = Add('4');
            var next = Add('5');
            SetSlots(720, 810);
            var rig = CreateRig();
            rig.OnCast = key =>
            {
                if (key == '5') SetSlot(4, 412);
                else if (newValues == 1) SetSlot(2, 411);
                else if (newValues == 2) SetSlots(720, 810, 411, 421);
            };
            rig.OnDelay = () =>
            {
                if (newValues == 1 && rig.Now == Start.AddMilliseconds(2300))
                    SetSlot(2, 0);
            };

            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Null(Load(failed));
            Assert.Equal((short)412, Load(next));
            Assert.Contains(_log, line => line.Contains(reason));
            if (newValues == 0)
                Assert.Contains(_log, line => line.Contains("while this buff is inactive"));
            rig.Now = Start.AddSeconds(5);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(new[] { (int)'4', '5' }, rig.Keys);
        }

        [Fact]
        public async Task RejectsValueAlreadyAssignedToADisabledBuff()
        {
            var learned = Add('4');
            learned.Enabled = false;
            Learn(learned, 4001);
            var unknown = Add('5');
            var rig = CreateRig();
            rig.OnCast = _ => SetSlots(4001);

            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Null(Load(unknown));
            Assert.Contains(_log, line => line.Contains("already assigned to another buff"));
        }

        [Theory]
        [InlineData("monitor", 0)]
        [InlineData("before", 0)]
        [InlineData("after", 1)]
        [InlineData("confirmation", 1)]
        public async Task PartialSlotReadNeverAuthorizesOrConfirmsLearning(string window, int casts)
        {
            var buff = Add('4');
            var rig = CreateRig();
            ulong unreadable = MemoryFixture.SlotAddress(5);
            var failAt = window switch
            {
                "monitor" => Start,
                "before" => Start.AddMilliseconds(200),
                "after" => Start.AddMilliseconds(2200),
                _ => Start.AddMilliseconds(2300)
            };
            rig.OnCast = _ => SetSlots(111);
            rig.OnDelay = () =>
            {
                if (rig.Now == failAt) _memory.FailRead(unreadable);
            };
            if (window == "monitor") _memory.FailRead(unreadable);

            await rig.TryCastAutoBuffsAsync(CancellationToken.None);
            Assert.Equal(casts, rig.Keys.Count);
            Assert.Null(Load(buff));
            _memory.RestoreRead(unreadable);
            SetSlots();
            rig.Now = Start.AddSeconds(5);
            await rig.TryCastAutoBuffsAsync(CancellationToken.None);
            Assert.Single(rig.Keys);
            Assert.Equal(casts == 0 ? (short?)111 : null, Load(buff));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SixNonzeroSlotsEvenWithDuplicatesDeferUntilASlotFrees(bool learned)
        {
            var buff = Add('4');
            if (learned) Learn(buff, 2000);
            var rig = CreateRig();
            SetSlots(12, 12, 100, 400, 600, -1);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            rig.Now = Start.AddSeconds(5);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Single(_log.Where(line => line.Contains("no free buff slot")));

            rig.Now = Start.AddSeconds(10);
            SetSlot(0, 0);
            rig.OnCast = _ => SetSlot(0, 2000);
            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Single(rig.Keys);
            Assert.Equal((short)2000, Load(buff));
        }

        [Fact]
        public void RenamingOrDisablingKeepsLearningAndResetOrKeyEditInvalidatesIt()
        {
            var buff = Add('4');
            Learn(buff, 1234);
            var row = new AutoBuffEntryViewModel(buff, () => Load(buff), _ => { });
            Guid original = buff.Id;
            Assert.Equal("Learned", row.CalibrationStatus);
            Assert.Contains("1234", row.CalibrationDetail);
            row.Name = "Updated";
            row.Enabled = false;
            row.Enabled = true;
            Assert.Equal(original, buff.Id);
            Assert.Equal("Learned", row.CalibrationStatus);

            row.ResetLearningCommand.Execute(null);
            Assert.NotEqual(original, buff.Id);
            Assert.Equal("Not learned", row.CalibrationStatus);
            Learn(buff, 3456);
            Guid reset = buff.Id;
            row.VirtualKey = '5';
            Assert.NotEqual(reset, buff.Id);
            row.VirtualKey = '4';
            Assert.Equal("Not learned", row.CalibrationStatus);
        }

        [Fact]
        public async Task StopsRetryingAfterTwoFailuresUntilLearnedValueReappears()
        {
            var buff = Add('4');
            Learn(buff, 1452);
            var rig = CreateRig();
            rig.OnCast = _ => SetSlots(412);
            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            DateTime firstFailure = rig.Now;
            for (int seconds = 5; seconds <= 25; seconds += 5)
            {
                rig.Now = firstFailure.AddSeconds(seconds);
                Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            }

            rig.Now = firstFailure.AddSeconds(30);
            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            rig.Now = firstFailure.AddMinutes(1).AddSeconds(5);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(2, rig.Keys.Count);
            Assert.Equal((short)1452, Load(buff));
            Assert.Contains(_log, line => line.Contains("Retry in 30 seconds"));
            Assert.Contains(_log, line => line.Contains("Renewal suspended"));

            SetSlots(1452);
            rig.Now = rig.Now.AddSeconds(5);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            SetSlots();
            rig.Now = rig.Now.AddSeconds(5);
            rig.OnCast = _ => SetSlots(1452);
            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal(3, rig.Keys.Count);
            Assert.Contains(_log, line => line.Contains("Rebuff successful"));
        }

        [Theory]
        [InlineData("stop")]
        [InlineData("pause")]
        [InlineData("death")]
        [InlineData("map")]
        [InlineData("cancel")]
        public async Task ActiveCastInterruptionsReleaseOwnershipAndDoNotSave(string cause)
        {
            var first = Add('4');
            var second = Add('5');
            using var token = new CancellationTokenSource();
            var rig = CreateRig();
            rig.OnCast = _ =>
            {
                SetSlots(1144);
                switch (cause)
                {
                    case "stop": rig.Stop(); break;
                    case "pause": rig.MobGroupingPausePredicate = () => true; break;
                    case "death": SetPlayerShort(BotConstants.MemoryOffsets.Hp, 0); break;
                    case "map": SetPlayerInt(BotConstants.MemoryOffsets.CurrentMap, 46); break;
                    case "cancel": token.Cancel(); break;
                }
            };
            if (cause == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.TryCastAutoBuffsAsync(token.Token));
            else
                Assert.True(await rig.TryCastAutoBuffsAsync(token.Token));

            Assert.False(rig.IsAutoBuffCastActive);
            Assert.Single(rig.Keys);
            Assert.Null(Load(first));
            Assert.Null(Load(second));
            Assert.Contains(_log, line => line.Contains("Learning failed"));
        }

        [Theory]
        [InlineData("focus")]
        [InlineData("pause")]
        [InlineData("combat")]
        [InlineData("DoT")]
        [InlineData("grouping")]
        [InlineData("recovery")]
        [InlineData("mana regen")]
        [InlineData("retarget")]
        [InlineData("town")]
        [InlineData("unknown town")]
        [InlineData("unknown HP")]
        [InlineData("unknown class")]
        public async Task BlockedStatesDeferWithoutConsumingLearningAttempt(string state)
        {
            var buff = Add('4');
            var rig = CreateRig();
            Action restore = () => { };
            switch (state)
            {
                case "focus":
                    rig.Foreground = false;
                    restore = () => rig.Foreground = true;
                    break;
                case "pause":
                    rig.MobGroupingPausePredicate = () => true;
                    restore = () => rig.MobGroupingPausePredicate = null;
                    break;
                case "combat":
                    SetPlayerInt(BotConstants.MemoryOffsets.TargetSelected, 51);
                    restore = () => SetPlayerInt(BotConstants.MemoryOffsets.TargetSelected, 0);
                    break;
                case "DoT":
                    rig.DotOpeningController.BeginEncounter(51, Class, 2, 500, Start);
                    restore = () => rig.DotOpeningController.Cancel("test");
                    break;
                case "grouping":
                    var controller = Field(typeof(MovementSystem), "_mobGroupingController").GetValue(rig)!;
                    var field = Field(controller.GetType(), "_state");
                    field.SetValue(controller, (int)MobGroupingState.GroupingMove);
                    restore = () => field.SetValue(controller, (int)MobGroupingState.Idle);
                    break;
                case "recovery":
                case "mana regen":
                case "retarget":
                    string fieldName = state switch
                    {
                        "recovery" => "_isUnstuckRoutineActive",
                        "mana regen" => "_manaRegenActive",
                        _ => "_combatRetargetAwaitingSelection"
                    };
                    Field(typeof(MovementSystem), fieldName).SetValue(rig, true);
                    restore = () => Field(typeof(MovementSystem), fieldName).SetValue(rig, false);
                    break;
                case "town":
                    SetPlayerInt(BotConstants.MemoryOffsets.IsInCity, 1);
                    restore = () => SetPlayerInt(BotConstants.MemoryOffsets.IsInCity, 0);
                    break;
                default:
                    ulong offset = state switch
                    {
                        "unknown town" => BotConstants.MemoryOffsets.IsInCity,
                        "unknown HP" => BotConstants.MemoryOffsets.Hp,
                        _ => BotConstants.MemoryOffsets.CurrentClassPlayer
                    };
                    _memory.FailRead(Player + offset);
                    restore = () => _memory.RestoreRead(Player + offset);
                    break;
            }

            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Empty(rig.Keys);
            Assert.Empty(rig.Delays);
            restore();
            rig.Now = Start.AddSeconds(5);
            rig.OnCast = _ => SetSlots(2104);
            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Equal((short)2104, Load(buff));
        }

        [Fact]
        public async Task LootBarrierAndActiveScanDeferBuffsAndNewLootWaitsForBuffLease()
        {
            Add('4');
            var memory = _memory.CreateService();
            using var loot = new LootSystem(memory, _log.Add);
            var rig = new Rig(memory, CreateController(), _log.Add) { LootSystemRef = loot };
            rig.OnCast = _ =>
            {
                Assert.False(loot.TryReserveInputForAutoBuff());
                Assert.True(loot.Update(CancellationToken.None).IsCompletedSuccessfully);
                SetSlots(2501);
            };

            long request = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.True(loot.HasActiveMandatoryLootRequest);
            loot.CancelMandatoryLootRequest(request, "test");
            var state = Field(typeof(LootSystem), "_lootState");
            var originalState = state.GetValue(loot);
            state.SetValue(loot, Enum.Parse(state.FieldType, "Scan"));
            Assert.False(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Empty(rig.Keys);
            state.SetValue(loot, originalState);

            Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
            Assert.Single(rig.Keys);
            Assert.True(loot.TryReserveInputForAutoBuff());
            loot.ReleaseInputForAutoBuff();
        }

        [Fact]
        public async Task CastOwnsCameraAndResetsStuckProgressWithoutRestartingW()
        {
            Add('4');
            var memory = _memory.CreateService();
            var camera = new WKeyCameraController(memory, _log.Add);
            try
            {
                var rig = new Rig(memory, CreateController(), _log.Add, camera);
                var another = new object();
                var movementProgress = Field(typeof(MovementSystem), "_lastMoveProgressTime");
                var waypointProgress = Field(typeof(MovementSystem), "_lastNavigationProgressTime");
                movementProgress.SetValue(rig, Start.AddSeconds(-10));
                waypointProgress.SetValue(rig, Start.AddSeconds(-10));
                rig.OnCast = _ =>
                {
                    Assert.False(camera.TryAcquireExternalCameraOwner(another));
                    SetSlots(346);
                };

                Assert.True(await rig.TryCastAutoBuffsAsync(CancellationToken.None));
                Assert.True(camera.TryAcquireExternalCameraOwner(another));
                camera.ReleaseExternalCameraOwner(another, restoreStationary: false);
                Assert.Equal(DateTime.MinValue, movementProgress.GetValue(rig));
                Assert.Equal(DateTime.MinValue, waypointProgress.GetValue(rig));
                Assert.Equal(0, rig.ForwardKeyDowns);
            }
            finally
            {
                camera.EndSession();
            }
        }

        private List<string> ValidationErrors() =>
            new BotProfileLoader(_log.Add).ValidateProfile(_profile).Where(error => error.Contains("Automatic Buffs")).ToList();

        private AutoBuffEntry Add(int key, PlayerClass playerClass = Class)
        {
            var settings = _profile.GetOrCreateAutoBuffSettings(playerClass);
            settings.Enabled = true;
            var buff = new AutoBuffEntry { Name = $"Buff {GameInput.FormatVirtualKey(key)}", VirtualKey = key };
            settings.Buffs.Add(buff);
            return buff;
        }

        private void Learn(AutoBuffEntry buff, short value) =>
            Assert.True(_store.TrySave(_profile.SavedProfilePath!, Class, buff.Id, buff.VirtualKey, value, out _));

        private short? Load(AutoBuffEntry buff) => _store.Load(_profile.SavedProfilePath!, Class, buff.Id, buff.VirtualKey);

        private AutoBuffController CreateController() =>
            Assert.IsType<AutoBuffController>(AutoBuffController.Create(_profile, _log.Add, _store));

        private Rig CreateRig(AutoBuffController? controller = null) =>
            new(_memory.CreateService(), controller ?? CreateController(), _log.Add);

        private void SetSlots(params short[] values)
        {
            for (int slot = 0; slot < 6; slot++)
                SetSlot(slot, slot < values.Length ? values[slot] : (short)0);
        }

        private void SetSlot(int slot, short value) => _memory.SetShort(MemoryFixture.SlotAddress(slot), value);
        private void SetPlayerShort(ulong offset, short value) => _memory.SetShort(Player + offset, value);
        private void SetPlayerInt(ulong offset, int value) => _memory.Set(Player + offset, BitConverter.GetBytes(value));
        private static FieldInfo Field(Type type, string name) => type.GetField(name, PrivateInstance)!;

        public void Dispose() => _directory.Delete(recursive: true);

        private sealed class Rig : MovementSystem
        {
            public Rig(GameMemoryService memory, AutoBuffController? controller, Action<string> log, WKeyCameraController? camera = null)
                : base(memory, log, 5000, 5000, MovementPrecision.Medium, initialMode: BotMode.OnlyMove,
                    wKeyCameraController: camera)
            {
                AutoBuffController = controller;
                InternalRepotEnabled = false;
            }

            public DateTime Now { get; set; } = Start;
            public bool Foreground { get; set; } = true;
            public Action<int>? OnCast { get; set; }
            public Action? OnDelay { get; set; }
            public List<int> Keys { get; } = new();
            public List<byte> PotionKeys { get; } = new();
            public List<DateTime> CastTimes { get; } = new();
            public List<int> Delays { get; } = new();
            public int ForwardKeyDowns { get; private set; }

            protected override DateTime AutoBuffNow => Now;
            protected override bool IsAutoBuffGameForeground() => Foreground;
            protected override bool SendAutoBuffKey(int key)
            {
                Keys.Add(key);
                CastTimes.Add(Now);
                OnCast?.Invoke(key);
                return true;
            }

            protected override void SendSpeedPotionInput(byte key, byte scan) => PotionKeys.Add(key);
            protected override Task DelayAutoBuffAsync(int milliseconds, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Delays.Add(milliseconds);
                Now = Now.AddMilliseconds(milliseconds);
                OnDelay?.Invoke();
                return Task.CompletedTask;
            }

            protected override void SendSkillThreeKeyEvent(bool keyUp) { }
            protected override void SendSkillFourKeyEvent(bool keyUp) { }
            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
            {
                if (!keyUp) ForwardKeyDowns++;
            }
        }
    }
}
