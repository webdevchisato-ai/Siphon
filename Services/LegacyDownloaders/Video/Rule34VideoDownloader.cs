using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Text.RegularExpressions;

namespace Siphon.Services.LegacyDownloaders.Video
{
    public class Rule34VideoDownloader
    {
        private string _path, _url;
        private DownloadJob _job;
        private readonly ILogger _logger;

        public Rule34VideoDownloader(string p, string u, DownloadJob job, ILogger logger)
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
                    _job.Status = $"Initializing Rule34Video Scraper (Try {attempt})...";
                    _logger.LogInformation($"Attempt {attempt}: Initializing Rule34Video Scraper for {_url}");

                    await new BrowserFetcher().DownloadAsync();

                    browser = await Puppeteer.LaunchAsync(new LaunchOptions
                    {
                        Headless = true,
                        Args = new[] { "--no-sandbox", "--proxy-server=socks5://127.0.0.1:9050" }
                    });

                    var page = await browser.NewPageAsync();

                    _job.Status = "Navigating to Video Page...";
                    _logger.LogInformation($"Attempt {attempt}: Navigating to {_url}");
                    await page.GoToAsync(_url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } }).WaitAsync(token);

                    // Extract Title for Filename
                    try
                    {
                        await page.WaitForSelectorAsync("h1.title_video", new WaitForSelectorOptions { Timeout = 5000 }).WaitAsync(token);
                        var h1 = await page.QuerySelectorAsync("h1.title_video");
                        name = SharedScraperLogic.SanitizeFileName(SharedScraperLogic.CleanTitle(await page.EvaluateFunctionAsync<string>("e => e.innerText", h1)), _path);
                        _logger.LogInformation($"Attempt {attempt}: Extracted video title '{name}'");
                    }
                    catch (Exception ex)
                    {
                        name = "R34Video_" + DateTime.Now.Ticks;
                        _logger.LogWarning(ex, $"Attempt {attempt}: Failed to extract title. Defaulting to '{name}'");
                    }

                    _job.Status = "Finding highest resolution...";

                    // Find all download links in the wrap div
                    var links = await page.QuerySelectorAllAsync(".row_spacer .wrap a.tag_item");
                    if (links.Length == 0) throw new Exception("No download links found on page.");

                    string bestUrl = null;
                    int maxRes = 0;

                    foreach (var link in links)
                    {
                        var txt = await page.EvaluateFunctionAsync<string>("e => e.innerText", link);
                        var href = await page.EvaluateFunctionAsync<string>("e => e.href", link);

                        // Match digits followed by 'p' (e.g., 720p, 1080p)
                        var m = Regex.Match(txt, @"(\d+)p");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int r))
                        {
                            if (r > maxRes)
                            {
                                maxRes = r;
                                bestUrl = href;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(bestUrl)) throw new Exception("Could not determine best resolution link.");

                    _job.Status = $"Selected {maxRes}p. Starting download...";
                    _logger.LogInformation($"Attempt {attempt}: Selected {maxRes}p resolution. Target URL: {bestUrl}");

                    await browser.CloseAsync();
                    browser = null;

                    token.ThrowIfCancellationRequested();

                    fullPath = Path.Combine(_path, $"{name}.mp4");
                    _job.FinalFilePath = fullPath;

                    // Using the specific video URL as the referer to bypass hotlink protection
                    await SharedScraperLogic.DownloadWithProgressAsync(bestUrl, fullPath, _url, name, attempt, _job, token);

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
                    _job.Status = $"R34Video Error: {ex.Message}. Retrying...";
                    await Task.Delay(2000, token);
                }
            }
        }
    }
}