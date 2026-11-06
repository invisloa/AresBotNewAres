using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Thrown when the repot sequence is about to click a predefined shop position but
    /// the shop window is no longer open (e.g. a missed click on the potion tab closed
    /// it). Every repot click is gated on <c>IsShopOpen()</c>: without the gate the
    /// absolute clicks land in the game world, where a left-click means "walk here" —
    /// which is exactly what used to send the character to random extreme spots.
    ///
    /// The workflow coordinator catches this exception, waits about a minute and then
    /// restarts the whole repot step from the top (teleport to city → walk to the repot
    /// point → open shop → sell → buy). No further clicks are sent from the aborted run.
    /// </summary>
    public sealed class ShopWindowClosedException : Exception
    {
        public ShopWindowClosedException(string message) : base(message) { }
    }
}
