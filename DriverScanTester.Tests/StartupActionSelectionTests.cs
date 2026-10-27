using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Focused regression tests for the map-based startup action selection
    /// (<see cref="BotStartupActionSelector"/>) and for the persistence of the new
    /// optional starting-map metadata.
    /// </summary>
    public sealed class StartupActionSelectionTests
    {
        private static string SaveDirectory => Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));

        private static string ProfileDirectory => Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedBotProfiles"));

        // ─────────────────────────── profile builders ───────────────────────────

        /// <summary>
        /// Mirrors the shipped MainCOT profile: Etana repot (map 35) → Etana travel path
        /// (map 35) → COT entry operation (no starting map) → COT ExpLoop (map 36).
        /// </summary>
        private static BotProfile MainCotProfile() => new()
        {
            Name = "StartupSelectionMainCOT",
            EnableStartPositionCheck = true,
            StartPositionX = 214,
            StartPositionY = 213,
            ProtectionMapNumber = 35,
            StartPositionTolerance = 5,
            FlowSteps = new List<BotFlowStep>
            {
                new()
                {
                    Type = BotFlowStepType.Repot,
                    StartMapNumber = 35,
                    RepotPaths = new List<BotRouteStep>
                    {
                        new() { PathFile = "Etana Repot 01.json", StartMapNumber = 35 },
                        new() { PathFile = "Etana Repot 02.json", StartMapNumber = 35 },
                        new() { PathFile = "Etana Repot 03.json", StartMapNumber = 35 }
                    }
                },
                new()
                {
                    Type = BotFlowStepType.Path,
                    StartMapNumber = 35,
                    Routes = new List<BotRouteStep>
                    {
                        new() { PathFile = "Etana_GoCOT_01.json", StartMapNumber = 35 },
                        new() { PathFile = "Etana_GoCOT_02.json", StartMapNumber = 35 }
                    }
                },
                new()
                {
                    Type = BotFlowStepType.Operation,
                    OperationName = "GoInsideCOT"
                },
                new()
                {
                    Type = BotFlowStepType.ExpLoop,
                    StartMapNumber = 36,
                    Routes = new List<BotRouteStep>
                    {
                        new()
                        {
                            PathFile = "COT LEVEL 1.json",
                            StartMapNumber = 36,
                            StartCheckEnabled = true,
                            StartPositionX = 128,
                            StartPositionY = 145,
                            ProtectionMapNumber = 36,
                            StartPositionTolerance = 5
                        }
                    }
                }
            }
        };

        // ─────────────────────────── selection tests ───────────────────────────

        [Fact]
        public void Map35SelectsTheFirstMatchingEtanaAction()
        {
            var selection = BotStartupActionSelector.Select(MainCotProfile(), 35);

            Assert.True(selection.HasSelection);
            Assert.Equal(BotStartupSelectionStatus.Selected, selection.Status);
            Assert.Equal(0, selection.FlowIndex);
            Assert.Equal(0, selection.RouteIndex);
            Assert.Equal(35, selection.StartMapNumber);
            Assert.Equal(BotFlowStepType.Repot, selection.Step!.Type);
            Assert.Contains("map 35", selection.Reason);
        }

        [Fact]
        public void Map36SelectsCotExpLoopAndSkipsEarlierNonMatchingActions()
        {
            var selection = BotStartupActionSelector.Select(MainCotProfile(), 36);

            Assert.True(selection.HasSelection);
            Assert.Equal(3, selection.FlowIndex);
            Assert.Equal(0, selection.RouteIndex);
            Assert.Equal(36, selection.StartMapNumber);
            Assert.Equal(BotFlowStepType.ExpLoop, selection.Step!.Type);
            Assert.Equal("COT LEVEL 1.json", selection.Step.Routes[0].PathFile);
        }

        [Fact]
        public void EarliestFlowStepWinsWhenSeveralActionsMatch()
        {
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    new()
                    {
                        Type = BotFlowStepType.Path,
                        StartMapNumber = 35,
                        Routes = new List<BotRouteStep> { new() { PathFile = "first.json", StartMapNumber = 35 } }
                    },
                    new()
                    {
                        Type = BotFlowStepType.ExpLoop,
                        StartMapNumber = 35,
                        Routes = new List<BotRouteStep> { new() { PathFile = "second.json", StartMapNumber = 35 } }
                    }
                }
            };

            var selection = BotStartupActionSelector.Select(profile, 35);

            Assert.True(selection.HasSelection);
            Assert.Equal(0, selection.FlowIndex);
            Assert.Equal(BotFlowStepType.Path, selection.Step!.Type);
        }

        [Fact]
        public void EarliestRouteWinsInsideTheSelectedStep()
        {
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    new()
                    {
                        Type = BotFlowStepType.ExpLoop,
                        StartMapNumber = 36,
                        Routes = new List<BotRouteStep>
                        {
                            new() { PathFile = "route-a.json", StartMapNumber = 36 },
                            new() { PathFile = "route-b.json", StartMapNumber = 36 }
                        }
                    }
                }
            };

            var selection = BotStartupActionSelector.Select(profile, 36);

            Assert.True(selection.HasSelection);
            Assert.Equal(0, selection.RouteIndex);
            Assert.Equal("route-a.json", selection.Step!.Routes[0].PathFile);
        }

        [Fact]
        public void RouteLevelMapOverridesTheStepDefault()
        {
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    new()
                    {
                        // Step default 35, but the second route explicitly starts on 36.
                        Type = BotFlowStepType.Path,
                        StartMapNumber = 35,
                        Routes = new List<BotRouteStep>
                        {
                            new() { PathFile = "etana.json" },
                            new() { PathFile = "cot.json", StartMapNumber = 36 }
                        }
                    }
                }
            };

            var map35 = BotStartupActionSelector.Select(profile, 35);
            Assert.True(map35.HasSelection);
            Assert.Equal(0, map35.RouteIndex);
            Assert.Equal("etana.json", map35.Step!.Routes[0].PathFile);

            var map36 = BotStartupActionSelector.Select(profile, 36);
            Assert.True(map36.HasSelection);
            Assert.Equal(1, map36.RouteIndex);
            Assert.Equal("cot.json", map36.Step!.Routes[1].PathFile);
        }

        [Fact]
        public void AlternativeRepotRoutesUseTheirOwnStartingMaps()
        {
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    new()
                    {
                        Type = BotFlowStepType.Repot,
                        RepotPaths = new List<BotRouteStep>
                        {
                            new() { PathFile = "etana-repot.json", StartMapNumber = 35 },
                            new() { PathFile = "cot-repot.json", StartMapNumber = 36 }
                        }
                    }
                }
            };

            var selection = BotStartupActionSelector.Select(profile, 36);

            Assert.True(selection.HasSelection);
            Assert.Equal(0, selection.FlowIndex);
            Assert.Equal(1, selection.RouteIndex);
            Assert.Equal(36, selection.StartMapNumber);
        }

        [Fact]
        public void SinglePathFileRouteIsSelectedAsRouteZero()
        {
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    new() { Type = BotFlowStepType.Path, PathFile = "single-travel.json", StartDelayMs = 250, StartMapNumber = 35 }
                }
            };

            var selection = BotStartupActionSelector.Select(profile, 35);

            Assert.True(selection.HasSelection);
            Assert.Equal(0, selection.FlowIndex);
            Assert.Equal(0, selection.RouteIndex);
            Assert.Equal(35, selection.StartMapNumber);

            // The effective route must keep the step's starting map.
            List<BotRouteStep>? pool = BotStartupActionSelector.GetRoutePool(profile.FlowSteps[0]);
            Assert.NotNull(pool);
            BotRouteStep effective = Assert.Single(pool!);
            Assert.Equal("single-travel.json", effective.PathFile);
            Assert.Equal(250, effective.StartDelayMs);
            Assert.Equal(35, effective.StartMapNumber);
        }

        [Fact]
        public void ALaterActionWithACloserPositionDoesNotOverrideTheFirstMatch()
        {
            // The player stands exactly on the LATER action's start position; the earlier
            // matching action must still win because proximity is only used later, inside
            // the selected route's MovementSystem resync.
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    new()
                    {
                        Type = BotFlowStepType.Repot,
                        StartMapNumber = 35,
                        RepotPaths = new List<BotRouteStep> { new() { PathFile = "etana-repot.json", StartMapNumber = 35 } }
                    },
                    new()
                    {
                        Type = BotFlowStepType.Path,
                        StartMapNumber = 35,
                        Routes = new List<BotRouteStep>
                        {
                            new()
                            {
                                PathFile = "etana-here.json",
                                StartMapNumber = 35,
                                StartCheckEnabled = true,
                                StartPositionX = 214,
                                StartPositionY = 213,
                                ProtectionMapNumber = 35
                            }
                        }
                    }
                }
            };

            var selection = BotStartupActionSelector.Select(profile, 35);

            Assert.True(selection.HasSelection);
            Assert.Equal(0, selection.FlowIndex);
            Assert.Equal(BotFlowStepType.Repot, selection.Step!.Type);
        }

        [Fact]
        public void AnActionWithoutAnExplicitStartingMapIsNotSelectedByName()
        {
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    // Looks like a COT location, but declares no starting map.
                    new() { Type = BotFlowStepType.Operation, OperationName = "GoInsideCOT" },
                    new() { Type = BotFlowStepType.Operation, OperationName = "Enter_COT" },
                    new()
                    {
                        Type = BotFlowStepType.ExpLoop,
                        StartMapNumber = 36,
                        Routes = new List<BotRouteStep> { new() { PathFile = "cot.json", StartMapNumber = 36 } }
                    }
                }
            };

            var selection = BotStartupActionSelector.Select(profile, 36);

            Assert.True(selection.HasSelection);
            Assert.Equal(2, selection.FlowIndex);
            Assert.Equal(BotFlowStepType.ExpLoop, selection.Step!.Type);
        }

        [Fact]
        public void AnOperationWithAnExplicitStartingMapIsSelectable()
        {
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    new()
                    {
                        Type = BotFlowStepType.Operation,
                        OperationName = "GoInsideCOT",
                        StartMapNumber = 35
                    }
                }
            };

            var selection = BotStartupActionSelector.Select(profile, 35);

            Assert.True(selection.HasSelection);
            Assert.Equal(0, selection.FlowIndex);
            Assert.Equal(-1, selection.RouteIndex);
            Assert.Equal(BotFlowStepType.Operation, selection.Step!.Type);
        }

        [Fact]
        public void AnUnsupportedMapFailsWithoutChoosingAnUnrelatedAction()
        {
            var selection = BotStartupActionSelector.Select(MainCotProfile(), 77);

            Assert.False(selection.HasSelection);
            Assert.Equal(BotStartupSelectionStatus.NoMatchingAction, selection.Status);
            Assert.Contains("map 77", selection.Reason);
            Assert.Contains("35", selection.Reason);
            Assert.Contains("36", selection.Reason);
        }

        [Fact]
        public void AProfileWithoutStartingMapsCannotBeStartedByMap()
        {
            var profile = new BotProfile
            {
                FlowSteps = new List<BotFlowStep>
                {
                    new() { Type = BotFlowStepType.Repot, RepotPaths = new List<BotRouteStep> { new() { PathFile = "repot.json" } } },
                    new() { Type = BotFlowStepType.ExpLoop, PathFile = "COT LEVEL 1.json" }
                }
            };

            Assert.False(BotStartupActionSelector.HasConfiguredStartupMaps(profile));

            var selection = BotStartupActionSelector.Select(profile, 36);
            Assert.Equal(BotStartupSelectionStatus.NoStartupMapConfigured, selection.Status);
            Assert.False(selection.HasSelection);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void AnInvalidObservedMapIsRejected(int map)
        {
            var selection = BotStartupActionSelector.Select(MainCotProfile(), map);

            Assert.Equal(BotStartupSelectionStatus.InvalidInput, selection.Status);
            Assert.Equal(-1, selection.FlowIndex);
        }

        [Fact]
        public void AnEmptyProfileIsRejected()
        {
            var selection = BotStartupActionSelector.Select(new BotProfile(), 36);

            Assert.Equal(BotStartupSelectionStatus.InvalidInput, selection.Status);
        }

        // ─────────────────────────── persistence tests ───────────────────────────

        [Fact]
        public void StartingMapMetadataSurvivesAJsonRoundTrip()
        {
            BotProfile profile = MainCotProfile();

            string json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            BotProfile? restored = JsonSerializer.Deserialize<BotProfile>(json);

            Assert.NotNull(restored);
            Assert.Equal(35, restored!.FlowSteps[0].StartMapNumber);
            Assert.All(restored.FlowSteps[0].RepotPaths, r => Assert.Equal(35, r.StartMapNumber));
            Assert.Equal(35, restored.FlowSteps[1].StartMapNumber);
            Assert.All(restored.FlowSteps[1].Routes, r => Assert.Equal(35, r.StartMapNumber));
            Assert.Equal(0, restored.FlowSteps[2].StartMapNumber);
            Assert.Equal(36, restored.FlowSteps[3].StartMapNumber);
            Assert.Equal(36, restored.FlowSteps[3].Routes[0].StartMapNumber);

            // The restored profile must select exactly like the original one.
            Assert.Equal(3, BotStartupActionSelector.Select(restored, 36).FlowIndex);
            Assert.Equal(0, BotStartupActionSelector.Select(restored, 35).FlowIndex);
        }

        [Fact]
        public void StartingMapMetadataSurvivesABotProfileLoaderSaveLoadRoundTrip()
        {
            string directory = ProfileDirectory;
            Directory.CreateDirectory(directory);
            string name = $"StartupSelectionRoundTrip_{Guid.NewGuid():N}";
            string path = Path.Combine(directory, name + ".json");
            var log = new List<string>();

            try
            {
                var loader = new BotProfileLoader(log.Add);

                // A single-file Path step is enough for the round trip (no path validation on save).
                var profile = new BotProfile
                {
                    Name = name,
                    FlowSteps = new List<BotFlowStep>
                    {
                        new()
                        {
                            Type = BotFlowStepType.Path,
                            PathFile = "unused.json",
                            StartDelayMs = 0,
                            StartMapNumber = 35,
                            CompletionMode = TravelRouteCompletionMode.FinalWaypoint
                        },
                        new()
                        {
                            Type = BotFlowStepType.ExpLoop,
                            StartMapNumber = 36,
                            Routes = new List<BotRouteStep>
                            {
                                new() { PathFile = "unused-exp.json", StartMapNumber = 36, ProtectionMapNumber = 36 }
                            }
                        }
                    }
                };

                Assert.True(loader.SaveProfile(profile));
                BotProfile? loaded = loader.LoadProfile(name);

                Assert.NotNull(loaded);
                Assert.Equal(35, loaded!.FlowSteps[0].StartMapNumber);
                Assert.Equal(36, loaded.FlowSteps[1].StartMapNumber);
                Assert.Equal(36, loaded.FlowSteps[1].Routes[0].StartMapNumber);
                Assert.Equal(0, loaded.FlowSteps[1].Routes[0].StartDelayMs);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void NegativeStartingMapsAreRejectedButZeroStaysValid()
        {
            string pathFile = CreateTemporaryPathFile();
            var log = new List<string>();

            try
            {
                var loader = new BotProfileLoader(log.Add);

                var valid = new BotProfile
                {
                    Name = "StartupMapValidationValid",
                    FlowSteps = new List<BotFlowStep>
                    {
                        new()
                        {
                            Type = BotFlowStepType.Path,
                            StartMapNumber = 0,
                            Routes = new List<BotRouteStep> { new() { PathFile = pathFile, StartMapNumber = 0 } }
                        }
                    }
                };
                Assert.Empty(loader.ValidateProfile(valid));

                var invalidStep = new BotProfile
                {
                    Name = "StartupMapValidationInvalidStep",
                    FlowSteps = new List<BotFlowStep>
                    {
                        new() { Type = BotFlowStepType.Path, PathFile = pathFile, StartMapNumber = -3 }
                    }
                };
                Assert.Contains(loader.ValidateProfile(invalidStep), e => e.Contains("starting map number cannot be negative"));

                var invalidRoute = new BotProfile
                {
                    Name = "StartupMapValidationInvalidRoute",
                    FlowSteps = new List<BotFlowStep>
                    {
                        new()
                        {
                            Type = BotFlowStepType.ExpLoop,
                            Routes = new List<BotRouteStep> { new() { PathFile = pathFile, StartMapNumber = -1 } }
                        }
                    }
                };
                Assert.Contains(loader.ValidateProfile(invalidRoute), e => e.Contains("starting map number cannot be negative"));
            }
            finally
            {
                try { File.Delete(Path.Combine(SaveDirectory, pathFile)); } catch { }
            }
        }

        [Fact]
        public void AProfileWithoutFlowStepsIsRejectedOnLoad()
        {
            string directory = ProfileDirectory;
            Directory.CreateDirectory(directory);
            string name = $"NoFlowSteps_{Guid.NewGuid():N}";
            string path = Path.Combine(directory, name + ".json");
            var log = new List<string>();

            try
            {
                // No FlowSteps at all: not a supported profile, so loading must fail with a
                // clear diagnostic instead of quietly converting anything.
                File.WriteAllText(path, "{" +
                    $"\"Name\":\"{name}\"," +
                    "\"CityToRepotPaths\":[{\"PathFile\":\"etana repot.json\"}]," +
                    "\"ExpLoop\":{\"PathFile\":\"COT LEVEL 1.json\"}" +
                    "}");

                var loader = new BotProfileLoader(log.Add);
                BotProfile? loaded = loader.LoadProfile(name);

                Assert.Null(loaded);
                Assert.Contains(log, line => line.Contains("has no flow steps and cannot be used"));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        // ─────────────────────────── helpers ───────────────────────────

        private static string CreateTemporaryPathFile()
        {
            Directory.CreateDirectory(SaveDirectory);
            string fileName = $"StartupSelection_{Guid.NewGuid():N}.json";
            File.WriteAllText(
                Path.Combine(SaveDirectory, fileName),
                "{\"Name\":\"startup-selection\",\"Points\":[" +
                "{\"X\":100,\"Y\":100,\"Precision\":2,\"Mode\":0}," +
                "{\"X\":101,\"Y\":100,\"Precision\":2,\"Mode\":0}]}");
            return fileName;
        }
    }
}
