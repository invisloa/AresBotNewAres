using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class ItemSellerScanCameraTests
    {
        [Theory]
        [InlineData(24, 24, true)]
        [InlineData(512, 384, true)]
        [InlineData(999, 743, true)]
        [InlineData(0, 384, false)]
        [InlineData(23, 384, false)]
        [InlineData(1000, 384, false)]
        [InlineData(512, 23, false)]
        [InlineData(512, 744, false)]
        public void ScanPointsStayInsideTheCameraSafeClientArea(int x, int y, bool expected)
        {
            Assert.Equal(expected,
                ItemSellerService.IsSellerScanPointWithinSafeArea(x, y, 1024, 768));
        }

        [Fact]
        public void ScanAreaRejectsInvalidClientDimensions()
        {
            Assert.False(ItemSellerService.IsSellerScanPointWithinSafeArea(0, 0, 0, 768));
            Assert.False(ItemSellerService.IsSellerScanPointWithinSafeArea(0, 0, 1024, 0));
        }

        [Fact]
        public void SellerDialogAppearingAfterFormerTwoHundredMillisecondWaitIsAccepted()
        {
            int elapsed = 0;
            bool opened = ItemSellerService.WaitForNpcDialog(
                () => elapsed >= 350,
                ms => elapsed += ms,
                timeoutMs: 700,
                pollMs: 25,
                out int waited);

            Assert.True(opened);
            Assert.Equal(350, waited);
        }

        [Fact]
        public void SellerDialogWaitStopsAtItsBoundWhenClickMisses()
        {
            int elapsed = 0;
            bool opened = ItemSellerService.WaitForNpcDialog(
                () => false,
                ms => elapsed += ms,
                timeoutMs: 700,
                pollMs: 25,
                out int waited);

            Assert.False(opened);
            Assert.Equal(700, waited);
        }
    }
}
