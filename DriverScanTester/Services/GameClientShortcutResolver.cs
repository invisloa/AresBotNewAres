using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DriverScanTester.Services
{
    internal static class GameClientShortcutResolver
    {
        private const string ExpectedShortcutName = "Play Legend of Ares.lnk";
        private const string OverrideEnvironmentVariable = "ARES_GAME_CLIENT_SHORTCUT";
        private const string GameDisplayName = "Legend of Ares";
        private const string LauncherExecutableName = "Launcher.exe";

        public static string? Find()
        {
            string? overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return Path.GetFullPath(overridePath);

            string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            string userStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            string commonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);

            var desktopDirectories = DistinctDirectories(userDesktop, commonDesktop);
            var startMenuDirectories = DistinctDirectories(
                Path.Combine(userStartMenu, "Programs"),
                Path.Combine(commonStartMenu, "Programs"));

            // Prefer the exact shortcut name in the standard locations.
            foreach (string directory in desktopDirectories.Concat(startMenuDirectories))
            {
                string candidate = Path.Combine(directory, ExpectedShortcutName);
                if (File.Exists(candidate))
                    return candidate;
            }

            // Some Windows installations or users rename their shortcuts. Search only
            // the Desktop folders and Start Menu Programs trees, avoiding unrelated files.
            IEnumerable<string> desktopShortcuts = EnumerateShortcuts(desktopDirectories, recursive: false);
            IEnumerable<string> startMenuShortcuts = EnumerateShortcuts(startMenuDirectories, recursive: true);
            string? shortcut = desktopShortcuts.Concat(startMenuShortcuts)
                .Where(path => Path.GetFileNameWithoutExtension(path).Contains("Legend of Ares", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Path.GetFileNameWithoutExtension(path).Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (shortcut != null)
                return shortcut;

            // A migrated PC may have the game installed without a desktop/start-menu
            // shortcut. Use the install location registered by the game's installer.
            return FindRegisteredLauncherExecutable();
        }

        public static string GetNotFoundMessage() =>
            $"Could not find the Legend of Ares launcher. Add its shortcut to the current user's Desktop, the Public Desktop, or Start Menu, repair the game installation so Windows registers it, or set the {OverrideEnvironmentVariable} environment variable to the shortcut or Launcher.exe full path.";

        private static string? FindRegisteredLauncherExecutable()
        {
            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Registry32 };

            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (RegistryView view in views)
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstallKey = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstallKey == null)
                        continue;

                    foreach (string subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        using RegistryKey? appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey == null ||
                            appKey.GetValue("DisplayName") is not string displayName ||
                            !displayName.Contains(GameDisplayName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? iconPath = GetLauncherPathFromDisplayIcon(appKey.GetValue("DisplayIcon") as string);
                        if (iconPath != null)
                            return iconPath;

                        string? installLocation = appKey.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(installLocation))
                        {
                            string launcherPath = Path.Combine(
                                Environment.ExpandEnvironmentVariables(installLocation.Trim().Trim('"')),
                                LauncherExecutableName);
                            if (File.Exists(launcherPath))
                                return Path.GetFullPath(launcherPath);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                    // A missing or inaccessible registry view should not prevent checking
                    // the other view or the normal shortcut locations.
                }
            }

            return null;
        }

        private static string? GetLauncherPathFromDisplayIcon(string? displayIcon)
        {
            if (string.IsNullOrWhiteSpace(displayIcon))
                return null;

            string path = displayIcon.Trim();
            if (path.StartsWith('"'))
            {
                int closingQuote = path.IndexOf('"', 1);
                if (closingQuote > 1)
                    path = path.Substring(1, closingQuote - 1);
            }
            else
            {
                int resourceIndexSeparator = path.LastIndexOf(',');
                if (resourceIndexSeparator > 0 &&
                    int.TryParse(path.Substring(resourceIndexSeparator + 1).Trim(), out _))
                {
                    path = path.Substring(0, resourceIndexSeparator).Trim();
                }
            }

            path = Environment.ExpandEnvironmentVariables(path);
            return File.Exists(path) &&
                   string.Equals(Path.GetFileName(path), LauncherExecutableName, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path)
                : null;
        }

        private static List<string> DistinctDirectories(params string[] directories) => directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        private static IEnumerable<string> EnumerateShortcuts(IEnumerable<string> directories, bool recursive)
        {
            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory))
                    continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(
                        directory,
                        "*.lnk",
                        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }

                foreach (string file in files)
                    yield return file;
            }
        }
    }
}
