using PuppeteerSharp;

namespace Siphon.Services.LegacyDownloaders
{
    public class PornHubDownloader
    {
        private string _path, _site, _url;
        private DownloadJob _job;
        private TaskCompletionSource<string> _signal;

        public PornHubDownloader(string p, string s, string u, DownloadJob job)
        {
            _path = p; _site = s; _url = u; _job = job;
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
                    await page.SetRequestInterceptionAsync(true);
                    page.Request += async (s, e) => {
                        if (e.Request.Url.Contains(".mp4") && !_signal.Task.IsCompleted)
                        {
                            _signal.TrySetResult(e.Request.Url);
                            await e.Request.AbortAsync();
                        }
                        else try { await e.Request.ContinueAsync(); } catch { }
                    };

                    await page.GoToAsync(_site, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } }).WaitAsync(token);

                    await page.WaitForSelectorAsync("input[type='text']").WaitAsync(token);
                    await page.TypeAsync("input[type='text']", _url);
                    await page.Keyboard.PressAsync("Enter");

                    await page.WaitForSelectorAsync(".download-buttons button", new WaitForSelectorOptions { Timeout = 20000 }).WaitAsync(token);
                    await Task.Delay(1000, token);

                    var tEl = await page.QuerySelectorAsync("#video-title");
                    if (tEl != null)
                    {
                        name = SharedScraperLogic.SanitizeFileName(await page.EvaluateFunctionAsync<string>("e => e.innerText", tEl), _path);
                    }

                    var btns = await page.QuerySelectorAllAsync(".download-buttons button");
                    IElementHandle best = null; int max = 0;
                    foreach (var b in btns)
                    {
                        var t = await page.EvaluateFunctionAsync<string>("e => e.innerText", b);
                        var parts = t.Split('p');
                        if (parts.Length > 0 && int.TryParse(parts[0].Trim(), out int r)) { if (r > max) { max = r; best = b; } }
                    }
                    if (best == null) best = btns.Last();

                    _job.Status = $"Found {max}p. Sniffing video URL...";
                    await page.EvaluateFunctionAsync("b => b.click()", best);

                    var res = await Task.WhenAny(_signal.Task, Task.Delay(45000, token));
                    if (res != _signal.Task) throw new Exception("Timeout waiting for video stream.");

                    string dlUrl = await _signal.Task;
                    await browser.CloseAsync(); browser = null;

                    token.ThrowIfCancellationRequested();

                    fullPath = Path.Combine(_path, $"{name}.mp4");
                    _job.FinalFilePath = fullPath;

                    await SharedScraperLogic.DownloadWithProgressAsync(dlUrl, fullPath, _site, name, attempt, _job, token);
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
    }
}