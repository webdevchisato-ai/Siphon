using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Text.RegularExpressions;
namespace Siphon.Services.LegacyDownloaders.Video
{
    public class Rule34Downloader
    {
        private string _path, _url;
        private DownloadJob _job;
        private readonly ILogger _logger;

        public Rule34Downloader(string p, string u, DownloadJob job, ILogger logger)
        {
            _path = p;
            _url = u;
            _job = job;
            _logger = logger;
        }

        public async Task Download(CancellationToken token)
        {
            int maxRetries = 5;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                token.ThrowIfCancellationRequested();

                string name = "Identifying...";
                string fullPath = null;
                IBrowser browser = null;

                try
                {
                    _job.Status = $"Initializing Rule34 Scraper (Try {attempt})...";
                    _logger.LogInformation($"Attempt {attempt}: Initializing Rule34 Scraper for {_url}");

                    await new BrowserFetcher().DownloadAsync();

                    browser = await Puppeteer.LaunchAsync(new LaunchOptions
                    {
                        Headless = true,
                        Args = new[] { "--no-sandbox", "--proxy-server=socks5://127.0.0.1:9050" }
                    });

                    var page = await browser.NewPageAsync();

                    // --- Stealth Tweaks ---
                    // Hide headless status and add standard headers
                    await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                    await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
                    {
                        { "Accept-Language", "en-US,en;q=0.9" }
                    });

                    _job.Status = "Navigating to Page...";
                    _logger.LogInformation($"Attempt {attempt}: Navigating to {_url}");

                    await page.GoToAsync(_url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle2 } }).WaitAsync(token);

                    // --- Cloudflare Check ---
                    var pageTitle = await page.GetTitleAsync();
                    if (pageTitle != null && pageTitle.Contains("Just a moment"))
                    {
                        _logger.LogWarning($"Attempt {attempt}: Cloudflare challenge detected. Attempting bypass...");
                        _job.Status = "Bypassing Cloudflare challenge...";

                        try
                        {
                            // Give the Turnstile widget a moment to load
                            var cfIframe = await page.WaitForSelectorAsync("iframe", new WaitForSelectorOptions { Timeout = 5000 });
                            if (cfIframe != null)
                            {
                                await Task.Delay(2000, token); // Brief pause before interaction
                                var box = await cfIframe.BoundingBoxAsync();
                                if (box != null)
                                {
                                    // Simulate a human click in the center of the Turnstile widget
                                    await page.Mouse.ClickAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
                                    _logger.LogInformation($"Attempt {attempt}: Clicked Cloudflare Turnstile widget.");
                                }
                            }
                        }
                        catch
                        {
                            _logger.LogDebug($"Attempt {attempt}: No visible Cloudflare iframe found to click, waiting for auto-resolve.");
                        }

                        try
                        {
                            // Wait for the video player or sidebar to appear, indicating CF passed
                            await page.WaitForSelectorAsync("#gelcomVideoPlayer, .link-list", new WaitForSelectorOptions { Timeout = 25000 }).WaitAsync(token);
                            _logger.LogInformation($"Attempt {attempt}: Cloudflare challenge bypassed successfully.");
                        }
                        catch (Exception)
                        {
                            throw new Exception("Cloudflare challenge did not resolve. Tor IP may be hard-blocked, or Headless detection triggered.");
                        }
                    }

                    // Extract Title for Filename
                    try
                    {
                        var titleElement = await page.GetTitleAsync();
                        string rawTitle = Regex.Replace(titleElement, @"Rule 34 - (.*) \| \d+.*", "$1");
                        name = SharedScraperLogic.SanitizeFileName(SharedScraperLogic.CleanTitle(rawTitle), _path);

                        if (name.Length > 150) name = name.Substring(0, 150);
                        _logger.LogInformation($"Attempt {attempt}: Extracted title '{name}'");
                    }
                    catch (Exception ex)
                    {
                        name = "Rule34_" + DateTime.Now.Ticks;
                        _logger.LogWarning(ex, $"Attempt {attempt}: Failed to extract title. Defaulting to '{name}'");
                    }

                    _job.Status = "Locating video source...";

                    // Wait for the video player or the sidebar links to appear in the DOM
                    await page.WaitForSelectorAsync("#gelcomVideoPlayer, .link-list", new WaitForSelectorOptions { Timeout = 15000 }).WaitAsync(token);

                    // Extract the direct link from the sidebar, the download button, or the video source
                    var videoSrc = await page.EvaluateFunctionAsync<string>(@"() => {
                        // 1. Try to find the 'Original image' link in the sidebar (most reliable for direct source)
                        const origLink = Array.from(document.querySelectorAll('.link-list a')).find(a => a.textContent.trim() === 'Original image');
                        if (origLink && origLink.href && origLink.href !== '' && !origLink.href.endsWith('#')) {
                            return origLink.href;
                        }

                        // 2. Try the JS populated download button
                        const downloadBtn = document.querySelector('#gelcomVideoPlayer_download');
                        if (downloadBtn && downloadBtn.href && downloadBtn.href !== '' && !downloadBtn.href.endsWith('#')) {
                            return downloadBtn.href;
                        }

                        // 3. Fallback: Grab it directly from the video tag
                        const v = document.querySelector('#gelcomVideoPlayer');
                        if (v && v.src) return v.src;
                        
                        // 4. Fallback: Grab it from the source tag inside the video
                        const s = document.querySelector('#gelcomVideoPlayer source');
                        return s ? s.src : null;
                    }");

                    if (string.IsNullOrEmpty(videoSrc))
                        throw new Exception("Could not extract a valid download link from the player.");

                    _logger.LogInformation($"Attempt {attempt}: Found video source: {videoSrc}");

                    await browser.CloseAsync();
                    browser = null;

                    token.ThrowIfCancellationRequested();

                    fullPath = Path.Combine(_path, $"{name}.mp4");
                    _job.FinalFilePath = fullPath;

                    _job.Status = "Starting download...";
                    await SharedScraperLogic.DownloadWithProgressAsync(videoSrc, fullPath, _url, name, attempt, _job, token);

                    _logger.LogInformation($"Attempt {attempt}: Download completed successfully for {_url}");

                    return;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning($"Download canceled for {_url}");
                    if (browser != null && !browser.IsClosed) await browser.CloseAsync();
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath)) try { File.Delete(fullPath); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Attempt {attempt}: Error scraping/downloading {_url}");
                    if (browser != null && !browser.IsClosed) await browser.CloseAsync();
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath)) try { File.Delete(fullPath); } catch { }

                    if (attempt == maxRetries)
                    {
                        _logger.LogError($"Max retries reached for {_url}. Failing job.");
                        throw;
                    }
                    _job.Status = $"Rule34 Error: {ex.Message}. Retrying...";
                    await Task.Delay(2000, token);
                }
            }
        }
    }
}