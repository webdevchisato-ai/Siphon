using Siphon.Services.LegacyDownloaders;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Siphon.Services
{
    public class VideoDownloader
    {
        private readonly IWebHostEnvironment _env;
        private readonly string _downloadPath;
        private readonly string _configPath;
        private string _phpSessId = "";
        private string _eprns = "";

        public VideoDownloader(IWebHostEnvironment env)
        {
            _env = env;
            _downloadPath = Path.Combine(_env.WebRootPath, "Pending");
            _configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "scraper_config.txt");
            if (!Directory.Exists(_downloadPath)) Directory.CreateDirectory(_downloadPath);
            LoadLegacyConfig();
        }

        public async Task ProcessDownload(DownloadJob job)
        {
            CancellationToken token = job.Cts.Token;

            try
            {
                await FetchMetadata(job, token);
                if (token.IsCancellationRequested) throw new OperationCanceledException();

                bool ytDlpSuccess = await TryYtDlp(job, token);
                if (ytDlpSuccess)
                {
                    job.Status = "Completed";
                    job.Progress = 100;
                    job.DownloadSpeed = "";
                    return;
                }

                if (token.IsCancellationRequested) throw new OperationCanceledException();

                job.Status = "yt-dlp failed. Reverting...";
                await Task.Delay(1000, token);

                // Legacy logic needs to be aware of tokens if you want to support cancel there
                // For now, we assume simple task wrapper
                if (job.Url.Contains("eporner.com"))
                    await new EpornerDownloader(_downloadPath, job.Url, job, _phpSessId, _eprns).Download();
                else if (job.Url.Contains("pornhub.com"))
                    await new PornHubDownloader(_downloadPath, "https://pornhubfans.com", job.Url, job).Download();
                else
                    await new UniversalDownloader(_downloadPath, job.Url, job).Download();

                job.Status = "Completed via Legacy";
                job.Progress = 100;
            }
            catch (OperationCanceledException)
            {
                // CLEANUP FILES
                if (!string.IsNullOrEmpty(job.FinalFilePath) && File.Exists(job.FinalFilePath))
                    try { File.Delete(job.FinalFilePath); } catch { }

                // Cleanup partials
                var partials = Directory.GetFiles(_downloadPath, $"*{job.Filename}*.part");
                foreach (var p in partials) try { File.Delete(p); } catch { }

                throw;
            }
        }

        private async Task FetchMetadata(DownloadJob job, CancellationToken token)
        {
            job.Status = "Fetching info...";
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--dump-json --skip-download --proxy socks5://127.0.0.1:9050 \"{job.Url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                string jsonOutput = "";
                process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) jsonOutput += e.Data; };

                process.Start();
                process.BeginOutputReadLine();
                // Pass token to WaitForExit
                await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(jsonOutput))
                {
                    var node = JsonNode.Parse(jsonOutput);
                    string title = node?["title"]?.ToString();
                    string thumb = node?["thumbnail"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(title)) job.Filename = SharedScraperLogic.SanitizeFileName(title);
                    if (!string.IsNullOrWhiteSpace(thumb)) job.ThumbnailUrl = thumb;
                }
            }
            catch { }
        }

        private async Task<bool> TryYtDlp(DownloadJob job, CancellationToken token)
        {
            int maxRetries = 3;
            for (int i = 1; i <= maxRetries; i++)
            {
                if (token.IsCancellationRequested) return false;

                job.Status = $"yt-dlp Attempt {i}/{maxRetries}";
                job.Progress = 0;

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "yt-dlp",
                        Arguments = $"--proxy socks5://127.0.0.1:9050 -o \"{_downloadPath}/%(title)s.%(ext)s\" \"{job.Url}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = new Process { StartInfo = startInfo };

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            // 1. Progress
                            var matchPct = Regex.Match(e.Data, @"\[download\]\s+(\d+\.?\d*)%");
                            if (matchPct.Success && double.TryParse(matchPct.Groups[1].Value, out double p))
                            {
                                job.Progress = p;
                                job.Status = $"Downloading";
                            }

                            // 2. NEW: Speed (Looks for "at 2.50MiB/s")
                            var matchSpd = Regex.Match(e.Data, @"at\s+(\d+\.?\d*\w+/s)");
                            if (matchSpd.Success)
                            {
                                job.DownloadSpeed = matchSpd.Groups[1].Value;
                            }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();

                    await process.WaitForExitAsync(token);

                    if (process.ExitCode == 0)
                    {
                        if (!string.IsNullOrEmpty(job.Filename))
                        {
                            job.FinalFilePath = Path.Combine(_downloadPath, $"{job.Filename}.mp4");
                        }
                        return true;
                    }
                }
                catch (OperationCanceledException) { throw; } // Let outer catch handle cleanup
                catch { }

                await Task.Delay(2000, token);
            }
            return false;
        }

        private void LoadLegacyConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var lines = File.ReadAllLines(_configPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("PHPSESSID=")) _phpSessId = trimmed.Substring(10).Trim();
                        if (trimmed.StartsWith("EPRNS=")) _eprns = trimmed.Substring(6).Trim();
                    }
                }
            }
            catch { }
        }
    }
}