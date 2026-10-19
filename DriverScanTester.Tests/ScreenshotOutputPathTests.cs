using System;
using System.IO;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// The manual loot diagnostic writes its annotated frames into the project's
    /// LootSSFolder so they are visible in the IDE/explorer. These tests cover the
    /// project-folder discovery used by that output path.
    /// </summary>
    public sealed class ScreenshotOutputPathTests
    {
        [Fact]
        public void FindsProjectFolderFromNestedBuildOutput()
        {
            string root = CreateTempDirectory();
            try
            {
                string project = Path.Combine(root, "DriverScanTester");
                Directory.CreateDirectory(project);
                File.WriteAllText(Path.Combine(project, "DriverScanTester.csproj"), "<Project />");

                string buildOutput = Path.Combine(root, "BuildTestOutput");
                Directory.CreateDirectory(buildOutput);

                string nestedBin = Path.Combine(root, "bin", "Debug", "net8.0-windows");
                Directory.CreateDirectory(nestedBin);

                Assert.Equal(project, ScreenshotService.TryFindProjectDirectory(buildOutput));
                Assert.Equal(project, ScreenshotService.TryFindProjectDirectory(nestedBin));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FindsProjectFolderWhenStartedFromItsOwnBinDirectory()
        {
            string root = CreateTempDirectory();
            try
            {
                string csproj = Path.Combine(root, "DriverScanTester.csproj");
                File.WriteAllText(csproj, "<Project />");

                string bin = Path.Combine(root, "bin", "Debug", "net8.0-windows");
                Directory.CreateDirectory(bin);

                Assert.Equal(root, ScreenshotService.TryFindProjectDirectory(bin));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DiagnosticCaptureFlagSelectsTheUnignoredProjectScreenshotsFolder()
        {
            bool previous = ScreenshotService.LootScanCaptureToProjectScreenshots;
            try
            {
                ScreenshotService.LootScanCaptureToProjectScreenshots = true;

                string diagnosticFolder = ScreenshotService.GetDiagnosticLootScanOutputDirectory();
                Assert.EndsWith(
                    Path.Combine("Screenshots", ScreenshotService.DiagnosticLootScreenshotsFolder),
                    diagnosticFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                Assert.Equal(diagnosticFolder, ScreenshotService.GetLootScanOutputDirectory());
            }
            finally
            {
                ScreenshotService.LootScanCaptureToProjectScreenshots = previous;
            }
        }

        [Fact]
        public void ReturnsNullForPublishedBuildWithoutSourceProject()
        {
            string root = CreateTempDirectory();
            try
            {
                string published = Path.Combine(root, "publish");
                Directory.CreateDirectory(published);

                Assert.Null(ScreenshotService.TryFindProjectDirectory(published));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "DriverScanTesterScreenshotTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
