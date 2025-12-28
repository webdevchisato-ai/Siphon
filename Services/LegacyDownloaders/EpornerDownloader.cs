using PuppeteerSharp;
using System.Text.RegularExpressions;

namespace Siphon.Services.LegacyDownloaders
{
    public class EpornerDownloader
    {
        private string _path, _url, _c1, _v1, _c2, _v2;
        private DownloadJob _job;
        private TaskCompletionSource<string> _signal;

        public EpornerDownloader(string p, string u, DownloadJob job, string s, string a)
            : this(p, u, job, "PHPSESSID", s, "EPRNS", a) { }

        public EpornerDownloader(string p, string u, DownloadJob job, string n1, string v1, string n2, string v2)
        {
            _path = p; _url = u; _job = job; _c1 = n1; _v1 = v1; _c2 = n2; _v2 = v2;
        }

        public async Task Download(CancellationToken token)
        {
            int maxRetries = 5;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                token.ThrowIfCancellationRequested();

                _signal = new TaskCompletionSource<string>();
                string name = "Identifying...";
                string fullPath = null;
                IBrowser browser = null;

                try
                {
                    _job.Status = $"Initializing Legacy Scraper (Try {attempt})...";

                    await new BrowserFetcher().DownloadAsync();

                    browser = await Puppeteer.LaunchAsync(new LaunchOptions
                    {
                        Headless = true,
                        Args = new[] { "--no-sandbox", "--proxy-server=socks5://127.0.0.1:9050" }
                    });

                    var page = await browser.NewPageAsync();
                    await EnableRequestSniffing(page);

                    if (!string.IsNullOrEmpty(_c1)) await page.SetCookieAsync(new CookieParam { Name = _c1, Value = _v1, Domain = ".eporner.com", Path = "/" });
                    if (!string.IsNullOrEmpty(_c2)) await page.SetCookieAsync(new CookieParam { Name = _c2, Value = _v2, Domain = ".eporner.com", Path = "/" });
                    await page.SetCookieAsync(new CookieParam { Name = "age_verified", Value = "1", Domain = ".eporner.com", Path = "/" });

                    _job.Status = "Navigating (Legacy)...";
                    await page.GoToAsync(_url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } }).WaitAsync(token);

                    if (await page.QuerySelectorAsync("#ageverifybox") != null) throw new Exception("Session invalid/Age verify failed.");

                    try
                    {
                        await page.WaitForSelectorAsync("h1", new WaitForSelectorOptions { Timeout = 5000 }).WaitAsync(token);
                        var h1 = await page.QuerySelectorAsync("h1");
                        name = SharedScraperLogic.SanitizeFileName(SharedScraperLogic.CleanTitle(await page.EvaluateFunctionAsync<string>("e => e.innerText", h1)));
                    }
                    catch { name = "Eporner_" + DateTime.Now.Ticks; }

                    var dlTrigger = await page.WaitForSelectorAsync("span[data-menutype='downloaddiv']", new WaitForSelectorOptions { Timeout = 10000 }).WaitAsync(token);
                    await dlTrigger.ClickAsync();
                    await page.WaitForSelectorAsync(".dloaddivcol", new WaitForSelectorOptions { Timeout = 5000 }).WaitAsync(token);

                    var links = await page.QuerySelectorAllAsync(".dloaddivcol a");
                    if (links.Length == 0) throw new Exception("No links found.");

                    IElementHandle best = null;
                    int maxRes = 0;
                    foreach (var link in links)
                    {
                        var txt = await page.EvaluateFunctionAsync<string>("e => e.innerText", link);
                        var m = Regex.Match(txt, @"(\d+)p");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int r)) { if (r > maxRes) { maxRes = r; best = link; } }
                    }
                    if (best == null) throw new Exception("No resolution found.");

                    _job.Status = $"Found {maxRes}p. Sniffing video URL...";
                    await page.EvaluateFunctionAsync("e => e.click()", best);

                    var result = await Task.WhenAny(_signal.Task, Task.Delay(30000, token));
                    if (result != _signal.Task) throw new Exception("Sniff Timeout");

                    string dlUrl = await _signal.Task;
                    await browser.CloseAsync(); browser = null;

                    token.ThrowIfCancellationRequested();

                    fullPath = Path.Combine(_path, $"{name}.mp4");
                    _job.FinalFilePath = fullPath;

                    await SharedScraperLogic.DownloadWithProgressAsync(dlUrl, fullPath, "https://www.eporner.com/", name, attempt, _job, token);

                    return;
                }
                catch (OperationCanceledException)
                {
                    if (browser != null && !browser.IsClosed) await browser.CloseAsync();
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath)) try { File.Delete(fullPath); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    if (browser != null && !browser.IsClosed) await browser.CloseAsync();
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath)) try { File.Delete(fullPath); } catch { }

                    if (attempt == maxRetries) throw;
                    _job.Status = $"Legacy Error: {ex.Message}. Retrying...";
                    await Task.Delay(2000, token);
                }
            }
        }

        async Task EnableRequestSniffing(IPage page)
        {
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (s, e) => {
                if (e.Request.Url.Contains(".mp4") && !_signal.Task.IsCompleted)
                {
                    _signal.TrySetResult(e.Request.Url);
                    await e.Request.AbortAsync();
                }
                else try { await e.Request.ContinueAsync(); } catch { }
            };
        }
    }
}