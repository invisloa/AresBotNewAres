using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public class WhitePotionPurchaseTests
    {
        [Fact]
        public async Task DelayedInventoryUpdatePreventsAnotherPurchase()
        {
            int stock = 1;
            int waitedMs = 0;
            var quantities = new List<int>();

            await RepotSystem.BuyWhitePotionsWithRetryAsync(
                4, () => stock,
                (quantity, _, _) =>
                {
                    quantities.Add(quantity);
                    return Task.CompletedTask;
                },
                (delayMs, _) =>
                {
                    waitedMs += delayMs;
                    if (waitedMs == BotConstants.Repot.WhitePotionPurchaseConfirmationMs)
                        stock = 4;
                    return Task.CompletedTask;
                },
                _ => { },
                CancellationToken.None);

            Assert.Equal(new[] { 3 }, quantities);
            Assert.Equal(4, stock);
        }

        [Fact]
        public async Task RetryBuysOnlyTheRemainingQuantityAfterPartialProgress()
        {
            int stock = 1;
            var quantities = new List<int>();

            await RepotSystem.BuyWhitePotionsWithRetryAsync(
                4, () => stock,
                (quantity, attempt, _) =>
                {
                    quantities.Add(quantity);
                    if (attempt == 1)
                        stock = 2;
                    else
                        stock += quantity;
                    return Task.CompletedTask;
                },
                (_, _) => Task.CompletedTask,
                _ => { },
                CancellationToken.None);

            Assert.Equal(new[] { 3, 2 }, quantities);
            Assert.Equal(4, stock);
        }

        [Fact]
        public async Task UnchangedStockUsesOnlyTheBoundedPurchaseAndDelayBudget()
        {
            int purchases = 0;
            int waitedMs = 0;

            await RepotSystem.BuyWhitePotionsWithRetryAsync(
                4, () => 1,
                (quantity, _, _) =>
                {
                    Assert.Equal(3, quantity);
                    purchases++;
                    return Task.CompletedTask;
                },
                (delayMs, _) =>
                {
                    waitedMs += delayMs;
                    return Task.CompletedTask;
                },
                _ => { },
                CancellationToken.None);

            Assert.Equal(BotConstants.Repot.WhitePotionPurchaseAttempts, purchases);
            Assert.Equal(BotConstants.Repot.WhitePotionPurchaseAttempts *
                         BotConstants.Repot.WhitePotionPurchaseConfirmationMs, waitedMs);
        }

        [Fact]
        public async Task CancellationDuringInventoryPollingPreventsARetry()
        {
            using var cancellation = new CancellationTokenSource();
            int purchases = 0;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RepotSystem.BuyWhitePotionsWithRetryAsync(
                    4, () => 1,
                    (_, _, _) =>
                    {
                        purchases++;
                        return Task.CompletedTask;
                    },
                    (_, _) =>
                    {
                        cancellation.Cancel();
                        return Task.CompletedTask;
                    },
                    _ => { },
                    cancellation.Token));

            Assert.Equal(1, purchases);
        }
    }
}
