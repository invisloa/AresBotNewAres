using System;
using System.Linq;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// The Tools tab "Test Operation — diagnostic tool" combo box is populated from
    /// <see cref="BotOperations.KnownNames"/>. These tests pin the diagnostic entries that
    /// must stay selectable there.
    /// </summary>
    public sealed class BotOperationsRegistryTests
    {
        [Fact]
        public void ScanForNpcIsRegisteredSoItCanBeRunFromTheDiagnosticTools()
        {
            Assert.True(BotOperations.IsKnown("ScanForNpc"));
            Assert.Contains("ScanForNpc", BotOperations.KnownNames);

            // The diagnostic combo resolves operation names case-insensitively.
            Assert.True(BotOperations.IsKnown("scanfornpc"));
        }

        [Fact]
        public void EveryKnownOperationNameResolvesToAnImplementation()
        {
            Assert.NotEmpty(BotOperations.KnownNames);
            Assert.All(BotOperations.KnownNames, name => Assert.True(BotOperations.Operations.ContainsKey(name)));
            Assert.Equal(
                BotOperations.KnownNames.Count,
                BotOperations.KnownNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void ScanForNpcRegistryEntryPointsAtTheNpcScanOperation()
        {
            // Guards against the diagnostic entry pointing at some other operation:
            // the registered delegate must be BotOperations.ScanForNpc itself.
            BotOperations.OperationDelegate registered = BotOperations.Operations["ScanForNpc"];
            Assert.Equal(nameof(BotOperations.ScanForNpc), registered.Method.Name);
        }
    }
}
