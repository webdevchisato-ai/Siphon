using PuppeteerSharp;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Net.Http; // Required for checking the API

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
                try
                {
                    await new BrowserFetcher().DownloadAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error downloading browser binaries: {ex.Message}");
                }
                // 1. Launch Browser
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

                        // Wait specifically for links to appear in the DOM
                        try
                        {
                            await page.WaitForFunctionAsync(@"() => {
                                return document.querySelectorAll('a[href*=""pixeldrain""]').length > 0;
                            }", new WaitForFunctionOptions { Timeout = 10000 });
                            _logger.LogInformation("[Hanime] Links rendered successfully.");
                        }
                        catch
                        {
                            _logger.LogWarning("[Hanime] Timed out waiting for links. They might be missing or already visible.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[Hanime] 'Get Download Links' button NOT found. Assuming links are visible.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Hanime] Interaction error: {ex.Message}");
                }

                // Scrape the links
                pixeldrainLink = await page.EvaluateFunctionAsync<string>(@"() => {
                    const buttons = Array.from(document.querySelectorAll('a[href*=""pixeldrain""]'));
                    if (buttons.length === 0) return null;

                    // Parse resolution
                    const getRes = (el) => {
                        const text = el.innerText || '';
                        if (text.includes('1080')) return 1080;
                        if (text.includes('720')) return 720;
                        if (text.includes('480')) return 480;
                        if (text.includes('360')) return 360;
                        return 0;
                    };

                    // Sort descending
                    buttons.sort((a, b) => getRes(b) - getRes(a));
                    return buttons[0].href;
                }");

                await browser.CloseAsync();
                browser = null;

                if (string.IsNullOrEmpty(pixeldrainLink))
                {
                    _logger.LogError("[Hanime] Could not find any Pixeldrain links.");
                    throw new Exception("No suitable download mirror found (Pixeldrain missing).");
                }

                _logger.LogInformation($"[Hanime] Found Pixeldrain Link: {pixeldrainLink}");

                // --- STEP 3: API Endpoint Fallback Strategy ---

                var match = Regex.Match(pixeldrainLink, @"pixeldrain\.com/(?:d|u|api/file|api/filesystem)/([a-zA-Z0-9]+)");
                if (!match.Success) throw new Exception($"Invalid Pixeldrain URL format: {pixeldrainLink}");

                string fileId = match.Groups[1].Value;

                // Define the two possible API endpoints
                string filesystemUrl = $"https://pixeldrain.com/api/filesystem/{fileId}";
                string fileUrl = $"https://pixeldrain.com/api/file/{fileId}";
                string finalDirectUrl = null;

                // Setup filename
                string safeName = SharedScraperLogic.SanitizeFileName(videoTitle);
                string fullPath = Path.Combine(_path, $"{safeName}.mp4");
                _job.FinalFilePath = fullPath;
                _job.Filename = safeName;

                // --- PROBE: Determine which endpoint works ---
                _logger.LogInformation("[Hanime] Probing API endpoints...");

                if (await ProbeUrl(filesystemUrl))
                {
                    _logger.LogInformation($"[Hanime] Filesystem API valid: {filesystemUrl}");
                    finalDirectUrl = filesystemUrl;
                }
                else if (await ProbeUrl(fileUrl))
                {
                    _logger.LogInformation($"[Hanime] Standard File API valid: {fileUrl}");
                    finalDirectUrl = fileUrl;
                }
                else
                {
                    _logger.LogError($"[Hanime] Both API endpoints returned 404 for ID {fileId}");
                    throw new Exception("File not found on Pixeldrain (404 on both endpoints).");
                }

                // --- STEP 4: Download ---
                _job.Status = "Downloading file...";
                _logger.LogInformation($"[Hanime] Saving to: {fullPath}");

                await SharedScraperLogic.DownloadWithProgressAsync(
                    finalDirectUrl,
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

        // Helper to check if a URL returns 200 OK (Head Request)
        private async Task<bool> ProbeUrl(string url)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    // Use HEAD to check without downloading
                    var request = new HttpRequestMessage(HttpMethod.Head, url);
                    var response = await client.SendAsync(request);
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}