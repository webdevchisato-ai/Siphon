using PuppeteerSharp;
using System.Text.RegularExpressions;

namespace Siphon.Services.LegacyDownloaders
{
    public class UniversalDownloader
    {
        private string _path, _url;
        private DownloadJob _job;
        private TaskCompletionSource<string> _signal;

        public UniversalDownloader(string p, string u, DownloadJob job)
        {
            _path = p; _url = u; _job = job;
        }

        public async Task Download()
        {
            _signal = new TaskCompletionSource<string>();
            string name = "Unknown";
            string fullPath = null;
            IBrowser browser = null;

            try
            {
                _job.Status = "Initializing Universal Scraper...";
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

                await page.GoToAsync(_url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });

                try
                {
                    var tEl = await page.QuerySelectorAsync("h1") ?? await page.QuerySelectorAsync("title");
                    if (tEl != null)
                    {
                        name = SharedScraperLogic.SanitizeFileName(await page.EvaluateFunctionAsync<string>("e => e.innerText", tEl));
                    }
                }
                catch { }

                _job.Status = "Scanning for download buttons...";
                var els = await page.QuerySelectorAllAsync("a, button");
                IElementHandle best = null; int max = 0;

                foreach (var el in els)
                {
                    var t = await page.EvaluateFunctionAsync<string>("e => e.innerText", el);
                    var h = await page.EvaluateFunctionAsync<string>("e => e.getAttribute('href')", el) ?? "";

                    if ((!string.IsNullOrWhiteSpace(t) && t.ToLower().Contains("download")) || h.ToLower().Contains("download"))
                    {
                        var m = Regex.Match(t, @"(\d{3,4})p");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int r)) { if (r > max) { max = r; best = el; } }
                        else if (best == null) best = el;
                    }
                }
                if (best == null) throw new Exception("No download button found via Universal scraper.");

                _job.Status = "Clicking download button...";
                await page.EvaluateFunctionAsync("e => e.click()", best);

                var res = await Task.WhenAny(_signal.Task, Task.Delay(30000));
                if (res != _signal.Task) throw new Exception("Timeout waiting for video stream.");

                string dlUrl = await _signal.Task;
                await browser.CloseAsync(); browser = null;

                fullPath = Path.Combine(_path, $"{name}.mp4");
                _job.FinalFilePath = fullPath;
                await SharedScraperLogic.DownloadWithProgressAsync(dlUrl, fullPath, _url, name, 1, _job);
            }
            catch (Exception ex)
            {
                if (browser != null && !browser.IsClosed) await browser.CloseAsync();
                if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath)) try { File.Delete(fullPath); } catch { }
                throw;
            }
        }
    }
}