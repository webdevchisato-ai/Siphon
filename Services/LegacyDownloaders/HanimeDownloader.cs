using PuppeteerSharp;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace Siphon.Services.LegacyDownloaders
{
    public class HanimeDownloader
    {
        private readonly string _path;
        private readonly string _url;
        private readonly DownloadJob _job;
        private readonly ILogger _logger;

        public HanimeDownloader(string savePath, string url, DownloadJob job, ILogger logger)
        {
            _path = savePath;
            _url = url;
            _job = job;
            _logger = logger;
        }

        public async Task Download(CancellationToken token)
        {
            _logger.LogInformation($"[Hanime] Starting Download-Page Scraper for: {_url}");
            _job.Status = "Initializing...";

            IBrowser browser = null;
            string downloadPageUrl = null;
            string pixeldrainLink = null;
            string videoTitle = "Hanime_Video";

            try
            {
                await new BrowserFetcher().DownloadAsync();

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
                page.DefaultNavigationTimeout = 60000;

                // --- STEP 1: Video Page -> Get Title & Download Link ---
                _logger.LogInformation("[Hanime] Navigating to video page...");
                await page.GoToAsync(_url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } }).WaitAsync(token);

                try
                {
                    var titleEl = await page.QuerySelectorAsync("h1.tv-title");
                    if (titleEl != null)
                        videoTitle = await page.EvaluateFunctionAsync<string>("e => e.innerText", titleEl);
                    else
                        videoTitle = await page.GetTitleAsync();

                    videoTitle = videoTitle.Replace("Watch ", "").Replace(" Hentai Video in 1080p HD - hanime.tv", "").Trim();
                }
                catch { }

                _logger.LogInformation($"[Hanime] Video Title: {videoTitle}");

                // Find the main 'Download' button to get to the downloads page
                var downloadBtn = await page.EvaluateFunctionAsync<string>(@"() => {
                    const anchors = Array.from(document.querySelectorAll('a'));
                    const dlLink = anchors.find(a => a.href.includes('/downloads/') || a.innerText.includes('DOWNLOAD'));
                    return dlLink ? dlLink.href : null;
                }");

                if (string.IsNullOrEmpty(downloadBtn))
                {
                    _logger.LogError("[Hanime] Could not find the 'Download' button/link on the video page.");
                    throw new Exception("Download button not found.");
                }

                downloadPageUrl = downloadBtn;
                _logger.LogInformation($"[Hanime] Found Download Page: {downloadPageUrl}");

                // --- STEP 2: Download Page -> Click Button -> Find Pixeldrain ---
                _job.Status = "Checking mirrors...";

                await page.GoToAsync(downloadPageUrl, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } }).WaitAsync(token);

                // Wait for the container to load
                await page.WaitForSelectorAsync(".content__dls", new WaitForSelectorOptions { Timeout = 10000 });

                // INTERACTION: Click "Get Download Links"
                try
                {
                    _logger.LogInformation("[Hanime] Looking for 'Get Download Links' button...");

                    var buttons = await page.XPathAsync("//div[contains(@class, 'btn__content') and contains(text(), 'Get Download Links')]");

                    if (buttons.Length > 0)
                    {
                        _logger.LogInformation("[Hanime] Button found. Clicking...");
                        await buttons[0].ClickAsync();

                        // Wait for links to appear
                        try
                        {
                            await page.WaitForFunctionAsync(@"() => {
                                return document.querySelectorAll('a[href*=""pixeldrain""]').length > 0;
                            }", new WaitForFunctionOptions { Timeout = 10000 });
                            _logger.LogInformation("[Hanime] Links rendered successfully.");
                        }
                        catch
                        {
                            _logger.LogWarning("[Hanime] Timed out waiting for links to render. They might be missing or already there.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[Hanime] 'Get Download Links' button NOT found. Assuming links are already visible.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Hanime] Interaction error: {ex.Message}");
                }

                // NOW scrape the links
                pixeldrainLink = await page.EvaluateFunctionAsync<string>(@"() => {
                    // Find all 'a' tags that have 'pixeldrain' in the href
                    const buttons = Array.from(document.querySelectorAll('a[href*=""pixeldrain""]'));
                    
                    if (buttons.length === 0) return null;

                    // Parse resolution from text
                    const getRes = (el) => {
                        const text = el.innerText || '';
                        if (text.includes('1080')) return 1080;
                        if (text.includes('720')) return 720;
                        if (text.includes('480')) return 480;
                        if (text.includes('360')) return 360;
                        return 0;
                    };

                    // Sort descending (highest resolution first)
                    buttons.sort((a, b) => getRes(b) - getRes(a));

                    return buttons[0].href;
                }");

                await browser.CloseAsync();
                browser = null;

                if (string.IsNullOrEmpty(pixeldrainLink))
                {
                    _logger.LogError("[Hanime] Could not find any Pixeldrain links. The button click may have failed or no mirrors are available.");
                    throw new Exception("No suitable download mirror found (Pixeldrain missing).");
                }

                _logger.LogInformation($"[Hanime] Found Pixeldrain Link: {pixeldrainLink}");

                // --- STEP 3: Convert to Direct API Link (FIXED) ---
                // Old: pixeldrain.com/api/file/{ID} (404 Not Found)
                // New: pixeldrain.com/api/filesystem/{ID} (Matches source HTML)

                var match = Regex.Match(pixeldrainLink, @"pixeldrain\.com/d/([a-zA-Z0-9]+)");
                if (!match.Success)
                {
                    throw new Exception($"Invalid Pixeldrain URL format: {pixeldrainLink}");
                }

                string fileId = match.Groups[1].Value;
                // CHANGED: Use 'filesystem' endpoint instead of 'file'
                string directUrl = $"https://pixeldrain.com/api/filesystem/{fileId}";

                string safeName = SharedScraperLogic.SanitizeFileName(videoTitle);
                string fullPath = Path.Combine(_path, $"{safeName}.mp4");

                _job.FinalFilePath = fullPath;
                _job.Filename = safeName;
                _job.Status = "Downloading file...";

                _logger.LogInformation($"[Hanime] Direct API URL: {directUrl}");
                _logger.LogInformation($"[Hanime] Saving to: {fullPath}");

                // --- STEP 4: Download ---
                await SharedScraperLogic.DownloadWithProgressAsync(
                    directUrl,
                    fullPath,
                    downloadPageUrl,
                    safeName,
                    1,
                    _job,
                    token
                );

                _job.Progress = 100;
                _job.Status = "Completed";
                _logger.LogInformation("[Hanime] Download completed successfully.");
            }
            catch (OperationCanceledException)
            {
                if (browser != null && !browser.IsClosed) await browser.CloseAsync();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[Hanime] Error: {ex.Message}");
                if (browser != null && !browser.IsClosed) await browser.CloseAsync();
                throw;
            }
        }
    }
}