using PuppeteerSharp;
using PuppeteerExtraSharp;
using PuppeteerExtraSharp.Plugins.ExtraStealth;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Siphon.Services.LegacyDownloaders
{
    public class HentaiDudeDownloader
    {
        private readonly string _path;
        private readonly string _initialUrl;
        private readonly DownloadJob _job;
        private readonly ILogger _logger;

        private readonly string _cfClearance;
        private readonly string _mangaViewCookie;
        private readonly string _userAgent;

        public HentaiDudeDownloader(string savePath, string url, DownloadJob job, ILogger logger, string cfClearance, string mangaViewCookie, string userAgent)
        {
            _path = savePath;
            _initialUrl = url;
            _job = job;
            _logger = logger;
            _cfClearance = cfClearance;
            _mangaViewCookie = mangaViewCookie;
            _userAgent = !string.IsNullOrEmpty(userAgent)
                ? userAgent
                : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36";
        }

        private TaskCompletionSource<string> _signal;

        public async Task Download(CancellationToken token)
        {
            _logger.LogInformation($"[HentaiDude] Starting STEALTH scraper for: {_initialUrl}");
            _job.Status = "Initializing Stealth Browser...";

            IBrowser browser = null;
            string videoTitle = "HentaiDude_Video";
            string directVideoUrl = null;
            string playerPageUrl = null; // Used for Referer
            _signal = new TaskCompletionSource<string>();

            try
            {
                var extra = new PuppeteerExtra();
                extra.Use(new StealthPlugin());

                await new BrowserFetcher().DownloadAsync();

                // 1. Launch Browser
                browser = await extra.LaunchAsync(new LaunchOptions
                {
                    Headless = true,
                    Args = new[] {
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-blink-features=AutomationControlled",
                        // "--proxy-server=socks5://127.0.0.1:9050" // Proxy disabled to match cookies
                    }
                });

                using (var page = await browser.NewPageAsync())
                {
                    await page.SetUserAgentAsync(_userAgent);

                    // 2. Set Cookies (Dynamic Domain)
                    var targetUri = new Uri(_initialUrl);
                    string domain = targetUri.Host;

                    if (!string.IsNullOrWhiteSpace(_cfClearance))
                    {
                        await page.SetCookieAsync(new CookieParam { Name = "cf_clearance", Value = _cfClearance, Domain = domain, Path = "/" });
                    }

                    if (!string.IsNullOrWhiteSpace(_mangaViewCookie))
                    {
                        string cookieName = "manga_view_cookie";
                        string cookieValue = _mangaViewCookie;
                        if (_mangaViewCookie.Contains("=")) { var parts = _mangaViewCookie.Split(new[] { '=' }, 2); cookieName = parts[0].Trim(); cookieValue = parts[1].Trim(); }
                        await page.SetCookieAsync(new CookieParam { Name = cookieName, Value = cookieValue, Domain = domain, Path = "/" });
                    }

                    // 3. Setup Sniffer
                    await page.SetRequestInterceptionAsync(true);
                    page.Request += async (s, e) => {
                        string rUrl = e.Request.Url;
                        if (!_signal.Task.IsCompleted &&
                           (rUrl.Contains("master.m3u8") || rUrl.Contains(".mp4") || rUrl.Contains("videoplayback")))
                        {
                            _logger.LogInformation($"[HentaiDude] Sniffer caught video URL: {rUrl}");
                            _signal.TrySetResult(rUrl);
                            await e.Request.ContinueAsync();
                        }
                        else { try { await e.Request.ContinueAsync(); } catch { } }
                    };

                    _job.Status = "Navigating...";
                    await page.GoToAsync(_initialUrl, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }, Timeout = 60000 }).WaitAsync(token);

                    // Check for Block
                    string pageTitle = await page.GetTitleAsync();
                    if (pageTitle.Contains("Just a moment") || pageTitle.Contains("Attention Required"))
                        throw new Exception("Cloudflare blocked the request.");

                    // 4. Series Detection Logic
                    // If we are on a series page (list of episodes) instead of a video page, we need to click the first episode.
                    var playerCheck = await page.QuerySelectorAsync("iframe[src*='player.php']");
                    if (playerCheck == null)
                    {
                        // Look for chapter list
                        var firstChapter = await page.QuerySelectorAsync(".wp-manga-chapter > a");
                        if (firstChapter != null)
                        {
                            string epUrl = await firstChapter.EvaluateFunctionAsync<string>("e => e.href");
                            _logger.LogInformation($"[HentaiDude] Series page detected. Redirecting to episode: {epUrl}");
                            _job.Status = "Navigating to episode...";

                            await page.GoToAsync(epUrl, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } }).WaitAsync(token);

                            // Update title after redirect
                            try
                            {
                                var titleEl = await page.QuerySelectorAsync("#chapter-heading") ?? await page.QuerySelectorAsync("h1");
                                if (titleEl != null) videoTitle = await page.EvaluateFunctionAsync<string>("e => e.innerText", titleEl);
                            }
                            catch { }
                        }
                        else
                        {
                            _logger.LogWarning("[HentaiDude] No player or chapter list found. Sniffer might timeout.");
                        }
                    }
                    else
                    {
                        // Just grab title
                        try
                        {
                            var titleEl = await page.QuerySelectorAsync("#chapter-heading") ?? await page.QuerySelectorAsync("h1");
                            if (titleEl != null) videoTitle = await page.EvaluateFunctionAsync<string>("e => e.innerText", titleEl);
                        }
                        catch { }
                    }

                    _logger.LogInformation($"[HentaiDude] Title: {videoTitle}");

                    // 5. Force Iframe Load
                    // Re-query in case we navigated
                    string iframeUrl = await page.EvaluateFunctionAsync<string>(@"() => {
                        const iframe = document.querySelector('iframe[src*=""player.php""]');
                        return iframe ? iframe.src : null;
                    }");

                    if (!string.IsNullOrEmpty(iframeUrl))
                    {
                        playerPageUrl = iframeUrl;
                        _job.Status = "Loading Player Frame...";
                        await page.GoToAsync(iframeUrl, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }, Referer = page.Url }).WaitAsync(token);
                    }
                    else
                    {
                        playerPageUrl = page.Url; // Fallback
                    }

                    _job.Status = "Waiting for video stream...";
                    var snifferTask = await Task.WhenAny(_signal.Task, Task.Delay(30000, token));

                    if (snifferTask == _signal.Task) directVideoUrl = await _signal.Task;
                    else throw new Exception("Timeout: Stream URL not found in network traffic.");
                }

                await browser.CloseAsync();
                browser = null;

                // 6. Download Phase
                string safeName = SharedScraperLogic.SanitizeFileName(videoTitle, _path);
                string finalPath = Path.Combine(_path, $"{safeName}.mp4");
                string tempPath = finalPath + ".part";

                _job.Filename = safeName;
                _job.FinalFilePath = finalPath;

                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }

                if (directVideoUrl.Contains(".m3u8"))
                {
                    _logger.LogInformation("[HentaiDude] HLS Detected. Stitching with FFmpeg...");
                    await DownloadHlsAsync(directVideoUrl, tempPath, playerPageUrl, token);
                }
                else
                {
                    _logger.LogInformation("[HentaiDude] Direct file detected.");
                    string downloadReferer = directVideoUrl.Contains("googlevideo") ? "https://hentaidude.xxx/" : _initialUrl;

                    await SharedScraperLogic.DownloadWithProgressAsync(
                        directVideoUrl, finalPath, downloadReferer, safeName, 1, _job, token
                    );
                    _job.Progress = 100;
                    _job.Status = "Completed";
                    return;
                }

                if (File.Exists(finalPath)) try { File.Delete(finalPath); } catch { }
                File.Move(tempPath, finalPath);

                _job.Progress = 100;
                _job.Status = "Completed";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HentaiDude] Failed: {ex.Message}");
                if (browser != null && !browser.IsClosed) await browser.CloseAsync();
                string safeNameCleanup = SharedScraperLogic.SanitizeFileName(videoTitle, _path);
                string tempCleanup = Path.Combine(_path, $"{safeNameCleanup}.mp4.part");
                if (File.Exists(tempCleanup)) try { File.Delete(tempCleanup); } catch { }
                throw;
            }
        }

        private async Task DownloadHlsAsync(string m3u8Url, string outputPath, string pageUrl, CancellationToken token)
        {
            _job.Status = "Starting FFmpeg...";
            _job.Progress = 0;

            string headers = $"Referer: {pageUrl}\r\nOrigin: https://hentaidude.xxx";

            // FFmpeg args: same as before, robust against HLS errors
            string args = $"-y -user_agent \"{_userAgent}\" -headers \"{headers}\" " +
                          $"-reconnect 1 -reconnect_at_eof 1 -reconnect_streamed 1 -reconnect_delay_max 5 -http_persistent 0 " +
                          $"-i \"{m3u8Url}\" " +
                          $"-f mp4 -movflags +faststart " +
                          $"-c copy -bsf:a aac_adtstoasc \"{outputPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            TimeSpan totalDuration = TimeSpan.Zero;
            var durationRegex = new Regex(@"Duration:\s(\d{2}):(\d{2}):(\d{2}\.\d{2})");
            var timeRegex = new Regex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})");
            // Regex to capture current file size (e.g. size= 1234kB)
            var sizeRegex = new Regex(@"size=\s*(\d+)(kB|MB|GB|B)?", RegexOptions.IgnoreCase);

            var recentLogs = new System.Collections.Generic.List<string>();

            // Speed calculation variables
            long lastSizeBytes = 0;
            DateTime lastLogTime = DateTime.UtcNow;

            process.ErrorDataReceived += (sender, e) => {
                if (string.IsNullOrEmpty(e.Data)) return;

                lock (recentLogs)
                {
                    recentLogs.Add(e.Data);
                    if (recentLogs.Count > 20) recentLogs.RemoveAt(0);
                }

                // Filter out expected retry errors from logs
                bool isRetryableError = e.Data.Contains("503 Service Temporarily Unavailable") ||
                                        e.Data.Contains("504 Gateway Time-out") ||
                                        e.Data.Contains("Failed to open segment");

                if ((e.Data.Contains("HTTP error") || e.Data.Contains("Forbidden") || e.Data.Contains("Failed")) && !isRetryableError)
                {
                    _logger.LogError($"[FFmpeg Error] {e.Data}");
                }

                // 1. Duration Parsing
                if (totalDuration == TimeSpan.Zero)
                {
                    var match = durationRegex.Match(e.Data);
                    if (match.Success)
                    {
                        if (TimeSpan.TryParse(match.Groups[0].Value.Replace("Duration: ", ""), out TimeSpan d))
                        {
                            totalDuration = d;
                            _logger.LogInformation($"[HentaiDude] Duration: {totalDuration}");
                        }
                    }
                }

                // 2. Progress & Speed Parsing
                var timeMatch = timeRegex.Match(e.Data);
                if (timeMatch.Success)
                {
                    // Calculate Percentage
                    string timeStr = timeMatch.Groups[0].Value.Replace("time=", "");
                    double percent = 0;
                    if (totalDuration.TotalSeconds > 0 && TimeSpan.TryParse(timeStr, out TimeSpan currentTime))
                    {
                        percent = (currentTime.TotalSeconds / totalDuration.TotalSeconds) * 100;
                        if (percent > 100) percent = 100;
                        _job.Progress = percent;
                    }

                    // Calculate Speed
                    var sizeMatch = sizeRegex.Match(e.Data);
                    string speedString = "";

                    if (sizeMatch.Success)
                    {
                        long currentBytes = ParseSizeToBytes(sizeMatch.Groups[1].Value, sizeMatch.Groups[2].Value);

                        // Delta calculation
                        double secondsElapsed = (DateTime.UtcNow - lastLogTime).TotalSeconds;

                        // Update speed only if meaningful time has passed (prevent divide by zero or noise)
                        if (secondsElapsed > 0.5)
                        {
                            // Delta Bytes / Delta Time
                            double speedBps = (currentBytes - lastSizeBytes) / secondsElapsed;
                            speedString = FormatSpeed(speedBps);

                            // Reset trackers
                            lastSizeBytes = currentBytes;
                            lastLogTime = DateTime.UtcNow;
                        }
                    }

                    // Update Status text
                    if (!string.IsNullOrEmpty(speedString))
                    {
                        _job.DownloadSpeed = speedString;
                        _job.Status = $"Downloading";
                    }
                    else
                    {
                        _job.Status = $"Downloading";
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                throw;
            }

            if (process.ExitCode != 0)
            {
                // Only dump logs if actual failure
                string lastErrors = string.Join("\n", recentLogs);
                _logger.LogError($"[FFmpeg Crash Dump]\n{lastErrors}");
                throw new Exception($"FFmpeg failed with code {process.ExitCode}. See logs for details.");
            }
        }

        // Helper to convert "1024kB" -> 1048576 bytes
        private long ParseSizeToBytes(string numberPart, string unitPart)
        {
            if (!long.TryParse(numberPart, out long size)) return 0;

            unitPart = unitPart.ToLower().Trim();

            if (unitPart.Contains("kb")) return size * 1024;
            if (unitPart.Contains("mb")) return size * 1024 * 1024;
            if (unitPart.Contains("gb")) return size * 1024 * 1024 * 1024;

            return size; // Assume bytes
        }

        // Helper to format bytes/s into readable string
        private string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "0 B/s";
            if (bytesPerSec > 1024 * 1024) return $"{bytesPerSec / (1024 * 1024):0.0} MB/s";
            if (bytesPerSec > 1024) return $"{bytesPerSec / 1024:0.0} KB/s";
            return $"{bytesPerSec:0} B/s";
        }
    }
}