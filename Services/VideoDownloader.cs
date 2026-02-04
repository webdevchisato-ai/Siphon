using Microsoft.AspNetCore.SignalR;
using PuppeteerSharp;
using Siphon.Services.LegacyDownloaders;
using System.Diagnostics;
using System.Net; // Required for WebProxy
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Siphon.Services
{
    public class VideoDownloader
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<VideoDownloader> _logger;
        private readonly string _downloadPath;
        private readonly string _configPath;
        private string _phpSessId = "";
        private string _eprns = "";
        private string _coomerSession = "";
        private string _kemonoSession = "";

        public VideoDownloader(IWebHostEnvironment env, ILogger<VideoDownloader> logger)
        {
            _env = env;
            _logger = logger;
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

                if (job.Url.Contains("eporner.com"))
                    await new EpornerDownloader(_downloadPath, job.Url, job, _phpSessId, _eprns).Download(token);
                else if (job.Url.Contains("pornhub.com"))
                    await new PornHubDownloader(_downloadPath, "https://pornhubfans.com", job.Url, job).Download(token);
                else if (job.Url.Contains("hanime.tv"))
                    await new HanimeDownloader(_downloadPath, job.Url, job, _logger).Download(token);
                else if (job.Url.Contains("coomer.st"))
                    await new CoomerDownloader(_downloadPath, job.Url, job, _coomerSession, _logger).Download(token);
                else if (job.Url.Contains("kemono.cr"))
                    await new KemonoDownloader(_downloadPath, job.Url, job, _kemonoSession, _logger).Download(token);
                else
                    await new UniversalDownloader(_downloadPath, job.Url, job).Download(token);

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
                await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(jsonOutput))
                {
                    var node = JsonNode.Parse(jsonOutput);
                    string title = node?["title"]?.ToString();
                    string thumb = node?["thumbnail"]?.ToString();

                    if (job.Url.Contains("rule34video.com"))
                    {
                        // Use the job ID to name the file locally
                        thumb = await TryDownloadRule34ThumbnailAsync(job.Url, job.Id);
                    }

                    if (job.Url.Contains("hanime.tv"))
                    {
                        thumb = await TryGetHanimeThumbnail(job.Url);
                    }

                    if (!string.IsNullOrWhiteSpace(title)) job.Filename = SharedScraperLogic.SanitizeFileName(title);
                    if (!string.IsNullOrWhiteSpace(thumb)) job.ThumbnailUrl = thumb;
                }
                else
                {
                    _logger.LogWarning($"yt-dlp metadata fetch failed with exit code, still attempting to get hanime info {process.ExitCode}");
                    string thumb = null;
                    string title = null;
                    if (job.Url.Contains("hanime.tv"))
                    {
                        title = ExtractHanimeTitleFromUrl(job.Url);
                        thumb = await TryGetHanimeThumbnail(job.Url);
                    }

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
                        Arguments = $"--proxy socks5://127.0.0.1:9050 -N 4 -f \"bv*+ba/b\" --merge-output-format mp4 -o \"{_downloadPath}/%(title)s.%(ext)s\" \"{job.Url}\"",
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
                                job.Progress = p;
                                job.Status = $"Downloading";
                            }

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
                catch (OperationCanceledException) { throw; }
                catch { }

                await Task.Delay(2000, token);
            }
            return false;
        }

        public async Task<string> TryGetHanimeThumbnail(string videoUrl)
        {
            _logger.LogInformation("Hanime Url Identified, Attempting to find preview image");

            IBrowser browser = null;
            try
            {
                try
                {
                    await new BrowserFetcher().DownloadAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error downloading browser binaries: {ex.Message}");
                    return null;
                }

                // Ensure browser is available
                _logger.LogInformation("Launching headless browser for thumbnail extraction...");

                // Launch a temporary headless browser
                browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = true,
                    Args = new[] {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-infobars",
                "--window-position=0,0",
                "--ignore-certificate-errors",
                "--ignore-certificate-errors-spki-list",
                "--user-agent=\"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\""
                }
                });

                var page = await browser.NewPageAsync();

                // Optimize: Block images/fonts since we only need the HTML/CSS text
                await page.SetRequestInterceptionAsync(true);
                page.Request += async (sender, e) =>
                {
                    if (e.Request.ResourceType == ResourceType.Image ||
                        e.Request.ResourceType == ResourceType.Font ||
                        e.Request.ResourceType == ResourceType.StyleSheet)
                    {
                        await e.Request.AbortAsync();
                    }
                    else
                    {
                        await e.Request.ContinueAsync();
                    }
                };

                // Navigate
                await page.GoToAsync(videoUrl, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });
                _logger.LogInformation("Page loaded, extracting thumbnail URL...");
                // Extract the thumbnail URL from the computed style of the poster div
                var thumbnailUrl = await page.EvaluateFunctionAsync<string>(@"() => {
                    // Try the main player poster
                    let el = document.querySelector('.poster');
                    // Fallback to the cover image if poster is missing
                    if (!el) el = document.querySelector('.content__data__cover');
                    if (!el) return null;
                    // Get the background-image property https://example.com/img.jpg
                    const bg = el.style.backgroundImage || window.getComputedStyle(el).backgroundImage;
                    if (bg && bg.includes('url')) {
                        // Strip the url() wrapper and quotes
                        return bg.replace(/^url\(['""]?/, '').replace(/['""]?\)$/, '');
                    }
                    return null;
                }");
                _logger.LogInformation($"Extracted thumbnail URL: {thumbnailUrl}");
                await browser.CloseAsync();
                return thumbnailUrl;
            }
            catch
            {
                if (browser != null) await browser.CloseAsync();
                return null;
            }
        }

        public string ExtractHanimeTitleFromUrl(string url)
        {
            try
            {
                // 1. Parse the URL
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                {
                    return "Unknown_Hanime_Video";
                }

                // 2. Get the last segment (e.g., "tamashii-insert-2")
                // uri.Segments returns parts like ["/", "videos/", "hentai/", "tamashii-insert-2"]
                string slug = uri.Segments.Last().Trim('/');

                // 3. Format it: Replace dashes with spaces and Title Case it
                // "tamashii-insert-2" -> "Tamashii Insert 2"
                string title = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(slug.Replace("-", " "));

                return title;
            }
            catch
            {
                return "Unknown_Hanime_Video";
            }
        }

        // --- UPDATED RULE34 LOGIC ---
        private async Task<string> TryDownloadRule34ThumbnailAsync(string videoUrl, string jobId)
        {
            _logger.LogInformation("Rule34Video Url Identified, Attempting to download preview image");

            // 1. Setup Local Directory
            string previewDir = Path.Combine(_env.WebRootPath, "PreviewImages");
            if (!Directory.Exists(previewDir)) Directory.CreateDirectory(previewDir);

            // 2. Prepare Default Fallback
            string defaultRemote = "https://rule34video.com/favicon.ico";

            // 3. Extract ID and Calculate Group
            long id = 0;
            long groupId = 0;
            string baseUrl = null;

            var match = Regex.Match(videoUrl, @"/video/(\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out id))
            {
                groupId = (id / 1000) * 1000;
                baseUrl = $"https://rule34video.com/contents/videos_screenshots/{groupId}/{id}";
            }
            else
            {
                _logger.LogInformation($"Failed to extract ID. Using default icon.");
            }

            string[] suffixes =
            {
                "preview_720p.mp4.jpg",
                "preview_1080p.mp4.jpg",
                "preview_480p.mp4.jpg",
                "preview_360p.mp4.jpg",
                "preview.mp4.jpg"
            };

            // 4. Configure Tor Proxy Client
            try
            {
                var proxy = new WebProxy("socks5://127.0.0.1:9050");
                var handler = new HttpClientHandler { Proxy = proxy };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

                // A. Try specific previews if we have an ID
                if (baseUrl != null)
                {
                    foreach (var suffix in suffixes)
                    {
                        string candidateUrl = $"{baseUrl}/{suffix}";
                        try
                        {
                            var request = new HttpRequestMessage(HttpMethod.Get, candidateUrl);
                            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                            if (response.IsSuccessStatusCode)
                            {
                                // Found valid image! Download it.
                                string extension = ".jpg";
                                string localFileName = $"{jobId}{extension}";
                                string savePath = Path.Combine(previewDir, localFileName);

                                using (var stream = await response.Content.ReadAsStreamAsync())
                                using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                                {
                                    await stream.CopyToAsync(fileStream);
                                }

                                _logger.LogInformation($"Downloaded preview to {localFileName}");
                                return $"/PreviewImages/{localFileName}";
                            }
                        }
                        catch { /* Try next suffix */ }
                    }
                }

                // B. Fallback: Download the favicon
                _logger.LogInformation("Downloading default favicon...");
                using var icoResponse = await client.GetAsync(defaultRemote);
                if (icoResponse.IsSuccessStatusCode)
                {
                    string localFileName = $"{jobId}.ico";
                    string savePath = Path.Combine(previewDir, localFileName);

                    using (var stream = await icoResponse.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                    {
                        await stream.CopyToAsync(fileStream);
                    }
                    return $"/PreviewImages/{localFileName}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error downloading Rule34 thumbnail via Tor: {ex.Message}");
            }

            // Absolute failsafe if Tor fails entirely
            return "/favicon.ico";
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
                        if (trimmed.StartsWith("COOMER_SESSION=")) _coomerSession = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("KEMONO_SESSION=")) _kemonoSession = trimmed.Substring(15).Trim();
                    }
                }
            }
            catch { }
        }
    }
}