using System.Diagnostics;

namespace Siphon.Services
{
    public class SystemBootstrapper : IHostedService
    {
        private readonly ILogger<SystemBootstrapper> _logger;
        private Process? _torProcess;
        private readonly object _lock = new();

        // Track blocked IPs to prevent reusing them
        private readonly Dictionary<string, DateTime> _blockedIps = new();
        private readonly TimeSpan _blockDuration = TimeSpan.FromMinutes(30);

        public SystemBootstrapper(ILogger<SystemBootstrapper> logger)
        {
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Bootstrapping System Dependencies (v2)...");

            // 1. Prepare Config
            await RebuildTorConfig();

            // 2. Start Tor
            StartTorProcess();

            // 3. Update yt-dlp
            _ = Task.Run(() => ExecuteCommand("pip", "install --upgrade yt-dlp --break-system-packages"));
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopTorProcess();
            return Task.CompletedTask;
        }

        public async Task RestartTorAsync(string? newExcludeIp = null)
        {
            _logger.LogWarning("Initiating Tor Restart Sequence...");

            // 1. USER REQUEST: Send HUP Signal FIRST
            try
            {
                _logger.LogInformation("Sending SIGHUP to Tor...");
                ExecuteCommand("sh", "-c \"pidof tor | xargs kill -HUP\"");
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                _logger.LogError($"HUP Signal failed: {ex.Message}");
            }

            // 2. Update Block List
            lock (_blockedIps)
            {
                var expiredIps = _blockedIps.Where(x => DateTime.UtcNow - x.Value > _blockDuration).Select(x => x.Key).ToList();
                foreach (var ip in expiredIps) _blockedIps.Remove(ip);

                if (!string.IsNullOrEmpty(newExcludeIp) && !_blockedIps.ContainsKey(newExcludeIp))
                {
                    _logger.LogWarning($"Adding IP to Exclude List: {newExcludeIp}");
                    _blockedIps[newExcludeIp] = DateTime.UtcNow;
                }
            }

            // 3. Hard Stop
            StopTorProcess();
            await Task.Delay(2000);

            // 4. Wipe Data
            CleanupTorData();

            // 5. Rebuild Config
            await RebuildTorConfig();

            // 6. Start Fresh
            StartTorProcess();

            await Task.Delay(8000);
        }

        private void CleanupTorData()
        {
            try
            {
                if (Directory.Exists("/var/lib/tor"))
                {
                    _logger.LogInformation("Clearing Tor Data Directory...");
                    Directory.Delete("/var/lib/tor", true);
                }
            }
            catch { }
        }

        private async Task RebuildTorConfig()
        {
            // Ensure Config Directory Exists (Fix for 'Reading config failed')
            if (!Directory.Exists("/etc/tor")) Directory.CreateDirectory("/etc/tor");

            // Ensure Data Directory Exists
            if (!Directory.Exists("/var/lib/tor"))
            {
                Directory.CreateDirectory("/var/lib/tor");
                ExecuteCommand("chmod", "700 /var/lib/tor");
            }

            string exclusionRule = "";
            lock (_blockedIps)
            {
                if (_blockedIps.Any())
                {
                    string ipList = string.Join(",", _blockedIps.Keys);
                    exclusionRule = $"ExcludeExitNodes {ipList}";
                }
            }

            // FIX: Added 'CookieAuthentication 0' so we can control it without passwords
            string torConfig = $@"
User root
SocksPort 0.0.0.0:9050
ControlPort 127.0.0.1:9051
CookieAuthentication 0
DataDirectory /var/lib/tor
Log notice stdout
MaxCircuitDirtiness 10
NewCircuitPeriod 10
{exclusionRule}
";
            await File.WriteAllTextAsync("/etc/tor/torrc", torConfig);
            _logger.LogInformation("Tor Config Rebuilt.");
        }

        private void StartTorProcess()
        {
            lock (_lock)
            {
                if (_torProcess != null && !_torProcess.HasExited) return;

                _logger.LogInformation("Starting Tor Process...");

                // Use the explicit config file we just wrote
                var psi = new ProcessStartInfo("tor", "-f /etc/tor/torrc")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _torProcess = new Process { StartInfo = psi };
                // Log standard output
                _torProcess.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogDebug($"[Tor] {e.Data}"); };
                // Filter out standard errors unless they are critical
                _torProcess.ErrorDataReceived += (s, e) => {
                    if (e.Data != null && !e.Data.Contains("Bootstrapped") && !e.Data.Contains("notice"))
                        _logger.LogWarning($"[Tor Msg] {e.Data}");
                };

                _torProcess.Start();
                _torProcess.BeginOutputReadLine();
                _torProcess.BeginErrorReadLine();
            }
        }

        private void StopTorProcess()
        {
            lock (_lock)
            {
                if (_torProcess != null && !_torProcess.HasExited)
                {
                    _logger.LogInformation("Killing Tor Process...");
                    try { _torProcess.Kill(); } catch { }
                    _torProcess.WaitForExit(3000);
                    _torProcess = null;
                }
            }
        }

        private void ExecuteCommand(string filename, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(filename, args) { CreateNoWindow = true, UseShellExecute = false };
                Process.Start(psi)?.WaitForExit();
            }
            catch { }
        }
    }
}