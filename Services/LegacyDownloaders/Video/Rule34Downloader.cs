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

                    _job.Status = "Navigating to Page...";
                    _logger.LogInformation($"Attempt {attempt}: Navigating to {_url}");

                    await page.GoToAsync(_url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle2 } }).WaitAsync(token);

                    // --- Cloudflare Check ---
                    var pageTitle = await page.GetTitleAsync();
                    if (pageTitle != null && pageTitle.Contains("Just a moment"))
                    {
                        _logger.LogWarning($"Attempt {attempt}: Cloudflare challenge detected. Waiting up to 30s for it to resolve...");
                        _job.Status = "Waiting for Cloudflare challenge...";

                        try
                        {
                            // Wait for the main body or video container to appear, indicating CF passed
                            await page.WaitForSelectorAsync("#gelcomVideoContainer", new WaitForSelectorOptions { Timeout = 30000 }).WaitAsync(token);
                            _logger.LogInformation($"Attempt {attempt}: Cloudflare challenge bypassed successfully.");
                        }
                        catch (Exception)
                        {
                            throw new Exception("Cloudflare challenge did not resolve automatically. Tor IP may be hard-blocked.");
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

                    _job.Status = "Locating download button...";

                    // Wait for the specific download button to appear in the DOM
                    await page.WaitForSelectorAsync("#gelcomVideoPlayer_download", new WaitForSelectorOptions { Timeout = 15000 }).WaitAsync(token);

                    // Extract the href from the download button, with a fallback to the video source
                    var videoSrc = await page.EvaluateFunctionAsync<string>(@"() => {
                        const downloadBtn = document.querySelector('#gelcomVideoPlayer_download');
                        
                        if (downloadBtn && downloadBtn.href && downloadBtn.href !== '' && !downloadBtn.href.endsWith('#')) {
                            return downloadBtn.href;
                        }

                        // Fallback: If the JS hasn't populated the href yet, grab it directly from the video tag
                        const v = document.querySelector('#gelcomVideoPlayer');
                        if (v && v.src) return v.src;
                        
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