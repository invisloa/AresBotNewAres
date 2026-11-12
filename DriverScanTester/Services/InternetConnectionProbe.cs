using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Checks whether the COMPUTER currently has a working internet connection. This is
    /// deliberately independent of the game client: the crash recovery uses it after it
    /// has killed the (possibly hung) client and before it decides to relaunch it.
    ///
    /// The check first verifies that Windows reports a usable network interface, then
    /// performs a short HTTP request and, if that is blocked, an ICMP ping. A captive
    /// portal or a disconnected cable therefore both count as "no internet".
    /// </summary>
    internal sealed class InternetConnectionProbe
    {
        private static readonly string[] ProbeUrls =
        {
            "http://www.msftconnecttest.com/connecttest.txt",
            "https://www.google.com/generate_204"
        };

        private static readonly string[] PingHosts = { "8.8.8.8", "1.1.1.1" };

        private static readonly HttpClient SharedClient = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private readonly HttpClient _client;

        public InternetConnectionProbe(HttpClient? client = null)
        {
            _client = client ?? SharedClient;
        }

        public async Task<bool> IsInternetAvailableAsync(CancellationToken token)
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                    return false;
            }
            catch
            {
                // A failing network-interface query is not proof of internet access;
                // continue with the HTTP/ICMP checks below.
            }

            foreach (string url in ProbeUrls)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await _client.GetAsync(
                        url,
                        HttpCompletionOption.ResponseHeadersRead,
                        token);
                    if (response.IsSuccessStatusCode)
                        return true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Try the next endpoint / the ICMP fallback.
                }
            }

            foreach (string host in PingHosts)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var ping = new Ping();
                    PingReply reply = await ping.SendPingAsync(host, 3_000);
                    if (reply.Status == IPStatus.Success)
                        return true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // The next host or the final "no internet" result.
                }
            }

            return false;
        }
    }
}
