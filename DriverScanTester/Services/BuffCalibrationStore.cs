using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    internal sealed class BuffCalibrationStore
    {
        private readonly string _directory;

        public BuffCalibrationStore(string? directory = null)
        {
            _directory = directory ?? Path.Combine(BotProfileLoader.ProfileDirectory, "BuffCalibrations");
        }

        public short? Load(string profilePath, PlayerClass playerClass, Guid id, int key)
        {
            Calibration identity = CreateIdentity(profilePath, playerClass, id, key);
            try
            {
                var calibration = JsonSerializer.Deserialize<Calibration>(File.ReadAllText(GetFilePath(identity)));
                if (calibration != null && calibration.Value != 0 &&
                    calibration with { Value = 0 } == identity)
                    return calibration.Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }

            return null;
        }

        public bool TrySave(string profilePath, PlayerClass playerClass, Guid id, int key, short value, out string? error)
        {
            Calibration calibration = CreateIdentity(profilePath, playerClass, id, key) with { Value = value };
            string destination = GetFilePath(calibration);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(_directory);
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, calibration);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: true);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        private static Calibration CreateIdentity(string profilePath, PlayerClass playerClass, Guid id, int key) =>
            new(Path.GetFullPath(profilePath).ToUpperInvariant(), playerClass, id, key, 0);

        private string GetFilePath(Calibration identity)
        {
            string profileHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ProfilePath)));
            return Path.Combine(_directory, $"{profileHash}-{(int)identity.PlayerClass}-{identity.Id:N}-{identity.VirtualKey:X2}.json");
        }

        private sealed record Calibration(string ProfilePath, PlayerClass PlayerClass, Guid Id, int VirtualKey, short Value);
    }
}
