using PuppeteerSharp;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Siphon.Services.LegacyDownloaders
{
    public class HanimeDownloader
    {
        private readonly string _path;
        private readonly string _url;
        private readonly DownloadJob _job;

        public HanimeDownloader(string savePath, string url, DownloadJob job)
        {
            _path = savePath;
            _url = url;
            _job = job;
        }

        public async Task Download(CancellationToken token)
        {
            _job.Status = "Initializing Hanime Scraper...";

            // 1. Setup Puppeteer (Direct Connection - No Proxy)
            // Hanime aggressively blocks Tor, so we use the container's direct IP.
            await new BrowserFetcher().DownloadAsync();

            var launchOptions = new LaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage"
                }
            };

            IBrowser browser = null;
            string m3u8Url = null;
            string videoTitle = "Hanime_Video";

            try
            {
                browser = await Puppeteer.LaunchAsync(launchOptions);
                var page = await browser.NewPageAsync();

                // 2. Setup Request Sniffing for HLS Manifest
                var tcs = new TaskCompletionSource<string>();
                await page.SetRequestInterceptionAsync(true);

                page.Request += async (sender, e) =>
                {
                    // Hanime streams usually contain .m3u8 in the URL
                    if (e.Request.Url.Contains(".m3u8") && !tcs.Task.IsCompleted)
                    {
                        tcs.TrySetResult(e.Request.Url);
                    }

                    try { await e.Request.ContinueAsync(); } catch { }
                };

                _job.Status = "Navigating to Hanime...";

                // Navigate
                await page.GoToAsync(_url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });

                // 3. Extract Title
                try
                {
                    await page.WaitForSelectorAsync("h1.tv-title", new WaitForSelectorOptions { Timeout = 5000 });
                    var titleEl = await page.QuerySelectorAsync("h1.tv-title");
                    var rawTitle = await page.EvaluateFunctionAsync<string>("e => e.innerText", titleEl);
                    videoTitle = SharedScraperLogic.SanitizeFileName(rawTitle);
                }
                catch
                {
                    videoTitle = $"Hanime_{DateTime.Now.Ticks}";
                }

                // 4. Wait for the stream URL
                var task = await Task.WhenAny(tcs.Task, Task.Delay(30000, token));

                if (task != tcs.Task)
                {
                    throw new Exception("Timeout: Could not detect .m3u8 stream.");
                }

                m3u8Url = await tcs.Task;
                _job.Status = "Stream found! Downloading...";
            }
            finally
            {
                if (browser != null) await browser.CloseAsync();
            }

            // 5. Download the stream using yt-dlp (Direct Mode)
            if (!string.IsNullOrEmpty(m3u8Url))
            {
                await DownloadStreamWithYtDlp(m3u8Url, videoTitle, token);
            }
        }

        private async Task DownloadStreamWithYtDlp(string streamUrl, string fileName, CancellationToken token)
        {
            _job.Filename = fileName;

            // Hanime checks headers. We pass a generic user agent and the referer.
            string args = $"--add-header \"Referer:{_url}\" --add-header \"User-Agent:Mozilla/5.0 (Windows NT 10.0; Win64; x64)\" -o \"{_path}/{fileName}.mp4\" \"{streamUrl}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = args,
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
                    var matchPct = Regex.Match(e.Data, @"\[download\]\s+(\d+\.?\d*)%");
                    if (matchPct.Success && double.TryParse(matchPct.Groups[1].Value, out double p))
                    {
                        _job.Progress = p;
                        _job.Status = "Downloading (HLS)";
                    }

                    var matchSpd = Regex.Match(e.Data, @"at\s+(\d+\.?\d*\w+/s)");
                    if (matchSpd.Success)
                    {
                        _job.DownloadSpeed = matchSpd.Groups[1].Value;
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();

            await process.WaitForExitAsync(token);

            if (process.ExitCode == 0)
            {
                _job.FinalFilePath = Path.Combine(_path, $"{fileName}.mp4");
            }
            else
            {
                throw new Exception($"yt-dlp stream download failed. Exit code {process.ExitCode}");
            }
        }
    }
}