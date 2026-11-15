using System.Text.Json;
using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.ViewModels;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class ClassRepotSettingsTests
    {
        [Theory]
        [InlineData(PlayerClass.Knight)]
        [InlineData(PlayerClass.Archer)]
        [InlineData(PlayerClass.Mage)]
        [InlineData(PlayerClass.Spear)]
        [InlineData(PlayerClass.Unknown)]
        public void LegacyProfile_UsesExistingValuesForEveryClass(PlayerClass playerClass)
        {
            var profile = JsonSerializer.Deserialize<BotProfile>("""
                {"HpBuyTarget":145,"ManaBuyTarget":75,"RedBuyTarget":7,"WhiteBuyTarget":8,
                 "MinHpPotions":9,"MinManaPotions":6,"MinHp":500,"MinMana":300,"MaxWeightRatio":0.8}
                """)!;
            var settings = profile.GetRepotSettings(playerClass);
            Assert.Equal(145, settings.HpBuyTarget);
            Assert.Equal(75, settings.ManaBuyTarget);
            Assert.Equal(7, settings.RedBuyTarget);
            Assert.Equal(8, settings.WhiteBuyTarget);
            Assert.Equal(9, settings.MinHpPotions);
            Assert.Equal(6, settings.MinManaPotions);
            Assert.Equal(500, settings.MinHp);
            Assert.Equal(300, settings.MinMana);
            Assert.Equal(0.8f, settings.MaxWeightRatio);
            Assert.Empty(profile.RepotSettingsByClass);
        }

        [Fact]
        public void ClassOverrides_AreIndependentAndSurviveJsonRoundTrip()
        {
            var profile = new BotProfile { HpBuyTarget = 120, ManaBuyTarget = 80 };
            foreach (var playerClass in new[] { PlayerClass.Knight, PlayerClass.Archer, PlayerClass.Mage, PlayerClass.Spear })
                profile.GetOrCreateRepotSettings(playerClass).HpBuyTarget = 200 + (int)playerClass;
            profile.GetOrCreateRepotSettings(PlayerClass.Archer).MinManaPotions = 17;

            var reloaded = JsonSerializer.Deserialize<BotProfile>(JsonSerializer.Serialize(profile))!;
            foreach (var playerClass in new[] { PlayerClass.Knight, PlayerClass.Archer, PlayerClass.Mage, PlayerClass.Spear })
            {
                Assert.Equal(200 + (int)playerClass, reloaded.GetRepotSettings(playerClass).HpBuyTarget);
                Assert.Equal(80, reloaded.GetRepotSettings(playerClass).ManaBuyTarget);
            }
            Assert.Equal(17, reloaded.GetRepotSettings(PlayerClass.Archer).MinManaPotions);
            Assert.Equal(120, reloaded.GetRepotSettings(PlayerClass.Unknown).HpBuyTarget);
        }

        [Fact]
        public void FirstClassEdit_CopiesAllSharedValuesWithoutChangingOtherClasses()
        {
            var profile = new BotProfile { HpBuyTarget = 150, ManaBuyTarget = 90, MinHpPotions = 12 };
            var archer = profile.GetOrCreateRepotSettings(PlayerClass.Archer);
            archer.HpBuyTarget = 50;
            profile.ManaBuyTarget = 40;

            Assert.Equal(50, profile.GetRepotSettings(PlayerClass.Archer).HpBuyTarget);
            Assert.Equal(90, archer.ManaBuyTarget);
            Assert.Equal(12, archer.MinHpPotions);
            Assert.Equal(150, profile.GetRepotSettings(PlayerClass.Knight).HpBuyTarget);
            Assert.Equal(40, profile.GetRepotSettings(PlayerClass.Knight).ManaBuyTarget);
        }

        [Fact]
        public void Editor_ClassSelectionEditsOnlySelectedClassAndRefreshesOnProfileChange()
        {
            var profile = new BotProfile { HpBuyTarget = 140, MinHpPotions = 11 };
            var editor = new ProfileEditorViewModel { CurrentProfile = profile };
            editor.SelectedRepotClass = PlayerClass.Archer;
            Assert.Equal(140, editor.HpBuyTarget);
            Assert.Empty(profile.RepotSettingsByClass);
            editor.HpBuyTarget = 65;
            editor.MinHpPotions = 3;
            editor.MaxWeightPercent = 85;
            editor.SelectedRepotClass = PlayerClass.Knight;
            Assert.Equal(140, editor.HpBuyTarget);
            Assert.Equal(11, editor.MinHpPotions);
            editor.HpBuyTarget = 160;
            editor.SelectedRepotClass = PlayerClass.Archer;
            Assert.Equal(65, editor.HpBuyTarget);
            Assert.Equal(3, editor.MinHpPotions);
            Assert.Equal(85, editor.MaxWeightPercent);
            editor.SelectedRepotClass = PlayerClass.Unknown;
            Assert.Equal(140, editor.HpBuyTarget);
            editor.HpBuyTarget = 125;
            Assert.Equal(125, profile.HpBuyTarget);
            Assert.Equal(65, profile.GetRepotSettings(PlayerClass.Archer).HpBuyTarget);
            editor.CurrentProfile = new BotProfile { HpBuyTarget = 42 };
            Assert.Equal(42, editor.HpBuyTarget);
        }

        [Fact]
        public void Validation_RejectsInvalidClassSettingsButAllowsZeroPurchaseTargets()
        {
            var profile = new BotProfile();
            var archer = profile.GetOrCreateRepotSettings(PlayerClass.Archer);
            archer.HpBuyTarget = -1;
            archer.MinManaPotions = -1;
            archer.MaxWeightRatio = 1.5f;
            var loader = new BotProfileLoader(_ => { });
            var errors = loader.ValidateProfile(profile);
            Assert.Contains(errors, e => e.Contains("Archer repot settings: potion buy targets"));
            Assert.Contains(errors, e => e.Contains("Archer repot settings: repot thresholds"));
            Assert.Contains(errors, e => e.Contains("Archer repot settings: MaxWeightRatio"));
            archer.HpBuyTarget = 0;
            archer.MinManaPotions = 0;
            archer.MaxWeightRatio = 0.9f;
            Assert.DoesNotContain(loader.ValidateProfile(profile), e => e.Contains("Archer repot settings"));
        }

        [Fact]
        public void Coordinator_AppliesLiveClassToThresholdsAndPurchases_AndFallsBackOnFailedRead()
        {
            const ulong moduleBase = 0x400000, playerBase = 0x100000;
            PlayerClass currentClass = PlayerClass.Archer;
            bool failRead = false;
            GameMemoryService.ReadMemoryDelegate read =
                (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                {
                    bytesRead = 0;
                    byte[] data;
                    if (address == moduleBase + BotConstants.MemoryOffsets.PlayerPtr)
                        data = BitConverter.GetBytes((uint)playerBase);
                    else if (address == playerBase + BotConstants.MemoryOffsets.CurrentClassPlayer && !failRead)
                        data = BitConverter.GetBytes((int)currentClass);
                    else
                        return false;
                    Buffer.BlockCopy(data, 0, buffer, 0, buffer.Length);
                    bytesRead = (uint)buffer.Length;
                    return true;
                };
            GameMemoryService.WriteMemoryDelegate write =
                (uint pid, ulong address, byte[] buffer, out uint bytesWritten) => { bytesWritten = 0; return false; };
            var memory = new GameMemoryService(999, read, write, moduleBase, 4, _ => { });
            var profile = new BotProfile { HpBuyTarget = 111, MinHpPotions = 9 };
            var archer = profile.GetOrCreateRepotSettings(PlayerClass.Archer);
            archer.HpBuyTarget = 60;
            archer.ManaBuyTarget = 30;
            archer.RedBuyTarget = 4;
            archer.WhiteBuyTarget = 5;
            archer.MinHpPotions = 3;
            archer.MinManaPotions = 2;
            archer.MinHp = 400;
            archer.MinMana = 200;
            archer.MaxWeightRatio = 0.8f;
            archer.MaxWeightValue = 3200;
            profile.GetOrCreateRepotSettings(PlayerClass.Knight).HpBuyTarget = 180;
            var repot = new RepotSystem(memory, _ => { });
            var detector = new RepotDetectorService(_ => { });
            var runner = new PathRunnerService(memory, _ => { });
            var context = new OperationContext(memory, runner, new ItemSellerService(memory, _ => { }), profile, _ => { }, () => { });
            var coordinator = new BotWorkflowCoordinator(memory, repot, detector, new SavedPathLoader(_ => { }),
                runner, new OperationRunnerService(context, _ => { }), profile, _ => { }, () => { });

            Assert.Equal(60, repot.HpBuyTarget);
            Assert.Equal(30, repot.ManaBuyTarget);
            Assert.Equal(4, repot.RedBuyTarget);
            Assert.Equal(5, repot.WhiteBuyTarget);
            Assert.Equal(3, detector.MinHpPotions);
            Assert.Equal(2, detector.MinManaPotions);
            Assert.Equal(400, detector.MinHp);
            Assert.Equal(200, detector.MinMana);
            Assert.Equal(0.8f, detector.MaxWeightRatio);
            Assert.Equal(3200, detector.MaxWeightValue);
            currentClass = PlayerClass.Knight;
            coordinator.ApplyClassRepotSettings();
            Assert.Equal(180, repot.HpBuyTarget);
            Assert.Equal(0, detector.MaxWeightValue);
            Assert.Equal(9, detector.MinHpPotions);
            failRead = true;
            coordinator.ApplyClassRepotSettings();
            Assert.Equal(111, repot.HpBuyTarget);
        }
        [Fact]
        public void ExactClassWeight_OverridesPercentageOnlyWhenSet()
        {
            var detector = new RepotDetectorService(_ => { })
            {
                MinHpPotions = 0,
                MinManaPotions = 0,
                MinHp = 0,
                MinMana = 0,
                MaxWeightRatio = 0.9f,
                MaxWeightValue = 3200
            };

            Assert.False(detector.NeedsRepot(Snapshot(3199, 10000)));
            Assert.True(detector.NeedsRepot(Snapshot(3200, 10000)));
            // The exact limit does not depend on the game's max weight being readable.
            Assert.True(detector.NeedsRepot(Snapshot(3200, 0)));

            // 0 disables the exact value and restores the percentage behaviour.
            detector.MaxWeightValue = 0;
            Assert.False(detector.NeedsRepot(Snapshot(3200, 10000)));
            Assert.True(detector.NeedsRepot(Snapshot(9000, 10000)));
        }

        private static GameSnapshot Snapshot(int currentWeight, int maxWeight) =>
            new(0f, 0f, 1, 1, false, hp: 5000, mana: 5000, hpPotions: 50, manaPotions: 50, currentWeight, maxWeight);

        [Fact]
        public void Editor_ExactWeightIsClassOnly_AndSurvivesJsonRoundTrip()
        {
            var profile = new BotProfile();
            var editor = new ProfileEditorViewModel { CurrentProfile = profile };

            // Shared defaults never get an exact value.
            Assert.False(editor.MaxWeightValueEditable);
            editor.MaxWeightValue = 3200;
            Assert.Equal(0, editor.MaxWeightValue);
            Assert.Empty(profile.RepotSettingsByClass);

            editor.SelectedRepotClass = PlayerClass.Archer;
            Assert.True(editor.MaxWeightValueEditable);
            Assert.Equal(0, editor.MaxWeightValue);
            editor.MaxWeightValue = 3200;
            Assert.Equal(3200, editor.MaxWeightValue);
            Assert.Equal(3200, profile.GetRepotSettings(PlayerClass.Archer).MaxWeightValue);
            Assert.Equal(0, profile.GetRepotSettings(PlayerClass.Knight).MaxWeightValue);

            // The percentage stays independently editable for the same class.
            editor.MaxWeightPercent = 70;
            Assert.Equal(3200, editor.MaxWeightValue);

            var reloaded = JsonSerializer.Deserialize<BotProfile>(JsonSerializer.Serialize(profile))!;
            Assert.Equal(3200, reloaded.GetRepotSettings(PlayerClass.Archer).MaxWeightValue);
            Assert.Equal(0, reloaded.GetRepotSettings(PlayerClass.Unknown).MaxWeightValue);
        }

        [Fact]
        public void Validation_RejectsNegativeOrTooLargeExactClassWeight()
        {
            var profile = new BotProfile();
            var loader = new BotProfileLoader(_ => { });
            var archer = profile.GetOrCreateRepotSettings(PlayerClass.Archer);

            archer.MaxWeightValue = -1;
            Assert.Contains(loader.ValidateProfile(profile), e => e.Contains("Archer repot settings: exact max weight"));

            // Weights are read as Int16, so larger values can never be reached.
            archer.MaxWeightValue = short.MaxValue + 1;
            Assert.Contains(loader.ValidateProfile(profile), e => e.Contains("Archer repot settings: exact max weight"));

            archer.MaxWeightValue = 3200;
            Assert.DoesNotContain(loader.ValidateProfile(profile), e => e.Contains("Archer repot settings"));
        }
    }
}
