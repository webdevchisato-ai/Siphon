using System.Net;
using System.Text.Json.Nodes;

namespace Siphon.Services
{
    public class TorProxyManager : IHostedService
    {
        private readonly ILogger<TorProxyManager> _logger;
        private readonly SystemBootstrapper _bootstrapper; // Injected dependency
        private const string TorProxyAddress = "socks5://127.0.0.1:9050";

        public string CurrentIp { get; private set; } = "Initializing...";
        public string CurrentCountry { get; private set; } = "Unknown";
        public bool IsRotating { get; private set; } = false;

        public TorProxyManager(ILogger<TorProxyManager> logger, SystemBootstrapper bootstrapper)
        {
            _logger = logger;
            _bootstrapper = bootstrapper;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Wait for Bootstrapper to finish initial Tor start
            await Task.Delay(8000, cancellationToken);
            _ = EnsureNonUkIp(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task RebuildCircuitAsync()
        {
            if (IsRotating) return;

            IsRotating = true;
            CurrentIp = "Restarting Service...";
            CurrentCountry = "Pending...";

            try
            {
                await _bootstrapper.RestartTorAsync();
                await EnsureNonUkIp(CancellationToken.None);
            }
            finally
            {
                IsRotating = false;
            }
        }

        private async Task EnsureNonUkIp(CancellationToken token)
        {
            bool isUk = true;
            IsRotating = true;

            while (isUk && !token.IsCancellationRequested)
            {
                try
                {
                    var proxy = new WebProxy(TorProxyAddress);
                    var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
                    using var client = new HttpClient(handler);
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36");
                    client.Timeout = TimeSpan.FromSeconds(20);

                    _logger.LogInformation("Checking IP location...");
                    var response = await client.GetStringAsync("http://ip-api.com/json/", token);

                    var json = JsonNode.Parse(response);
                    string countryCode = json?["countryCode"]?.ToString() ?? "UNKNOWN";
                    string queryIp = json?["query"]?.ToString() ?? "UNKNOWN";

                    _logger.LogInformation($"Current IP: {queryIp} | Country: {countryCode}");

                    if (countryCode == "GB" || countryCode == "UK")
                    {
                        CurrentIp = queryIp;
                        CurrentCountry = $"{countryCode} (Blocked)";
                        _logger.LogWarning("UK IP Detected! Restarting Tor Service...");

                        await _bootstrapper.RestartTorAsync();
                    }
                    else
                    {
                        CurrentIp = queryIp;
                        CurrentCountry = countryCode;
                        _logger.LogInformation("Tor Connection Established with Non-UK IP.");
                        isUk = false;
                    }
                }
                catch (Exception ex)
                {
                    CurrentIp = "Error";
                    CurrentCountry = "Retrying...";
                    _logger.LogError($"IP Check failed: {ex.Message}. Restarting Tor and retrying...");

                    await _bootstrapper.RestartTorAsync();
                }
            }
            IsRotating = false;
        }
    }
}