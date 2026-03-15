using PuppeteerSharp;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace Siphon.Services.LegacyDownloaders.Video
{
    public class RedditDownloader
    {
        private string _downloadPath;
        private string _url;
        private DownloadJob _job;
        private string _sessionCookie;
        private readonly ILogger _logger;
        private readonly IWebHostEnvironment _env;

        public RedditDownloader(string path, string url, DownloadJob job, string sessionCookie, ILogger logger, IWebHostEnvironment env)
        {
            _downloadPath = path;
            _url = url;
            _job = job;
            _sessionCookie = sessionCookie;
            _logger = logger;
            _env = env;
        }

        public async Task Download(CancellationToken token)
        {
            _job.Status = "Initializing Reddit Browser...";

            if (!_url.Contains("reddit.com/r/") || !_url.Contains("/comments/"))
                throw new Exception("Invalid URL. Must be a specific Reddit post link.");

            string[] videoUrls = null;
            IBrowser browser = null;

            try
            {
                await new BrowserFetcher().DownloadAsync();

                browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = true,
                    Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
                });

                using (var page = await browser.NewPageAsync())
                {
                    await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36");

                    if (!string.IsNullOrWhiteSpace(_sessionCookie))
                    {
                        var uri = new Uri(_url);
                        var domain = uri.Host;

                        await page.SetCookieAsync(new CookieParam
                        {
                            Name = "reddit_session", // <-- Updated to Reddit's auth cookie name
                            Value = _sessionCookie,
                            Domain = $".{domain}",
                            Path = "/",
                            Secure = true,
                            SameSite = SameSite.Lax
                        });
                    }

                    _job.Status = "Loading page...";
                    await page.GoToAsync(_url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });

                    _job.Status = "Extracting metadata...";

                    videoUrls = await page.EvaluateFunctionAsync<string[]>(@"async () => {
                        let urls = [];
                        
                        // 1. Native Reddit Video
                        const videoSources = document.querySelectorAll('shreddit-player source, video source');
                        videoSources.forEach(source => {
                            if (source.src) urls.push(source.src);
                        });

                        // 2. Shreddit Post content-href (Catch-all for embeds like RedGIFs)
                        const post = document.querySelector('shreddit-post');
                        if (post) {
                            const contentHref = post.getAttribute('content-href');
                            if (contentHref) urls.push(contentHref);
                        }

                        // 3. Fallback to shreddit-screenview-data
                        const screenData = document.querySelector('shreddit-screenview-data');
                        if (screenData) {
                            try {
                                const data = JSON.parse(screenData.getAttribute('data'));
                                if (data && data.post && data.post.url) urls.push(data.post.url);
                            } catch (e) {}
                        }
                        
                        // Filter for common video domains/extensions
                        const filteredUrls = urls.filter(url => 
                            url.includes('v.redd.it') || 
                            url.includes('redgifs.com') || 
                            url.endsWith('.mp4') || 
                            url.endsWith('.m3u8') || 
                            url.endsWith('.webm')
                        );

                        return [...new Set(filteredUrls)];
                    }");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Browser Error: {ex.Message}");
            }
            finally
            {
                if (browser != null) await browser.CloseAsync();
            }

            if (videoUrls == null || videoUrls.Length == 0)
                throw new Exception("No video files or valid embeds found on this Reddit post.");

            var videosToDownload = new List<(string Path, string Name)>();

            foreach (var vUrl in videoUrls)
            {
                var uri = new Uri(_url);
                var segments = uri.Segments;
                string postId = segments.Length > 3 ? segments[3].Trim('/') : "reddit_video";

                string nameSuffix = vUrl.Contains("redgifs.com") ? "_redgifs" : "";

                videosToDownload.Add((vUrl, $"{postId}{nameSuffix}"));
            }

            int count = 1;
            int total = videosToDownload.Count;

            foreach (var video in videosToDownload)
            {
                token.ThrowIfCancellationRequested();

                string downloadUrl = video.Path;

                // Process through RedGIFs resolver if applicable before downloading
                downloadUrl = await SharedScraperLogic.ResolveRedGifsUrlAsync(downloadUrl, token);

                string nameWithoutExt = video.Name;

                if (total > 1) nameWithoutExt = $"{nameWithoutExt}_{count}";

                string cleanName = SharedScraperLogic.SanitizeFileName(nameWithoutExt, _downloadPath);
                string ext = ".mp4";

                string rawExt = Path.GetExtension(new Uri(downloadUrl).LocalPath);
                if (!string.IsNullOrEmpty(rawExt) && IsVideo(rawExt))
                {
                    ext = rawExt;
                }

                string finalFileName = $"{cleanName}{ext}";
                string fullFilePath = Path.Combine(_downloadPath, finalFileName);

                _job.Filename = cleanName;
                _job.FinalFilePath = fullFilePath;
                _job.Status = (total > 1) ? $"Downloading {count}/{total}: {cleanName}" : $"Downloading: {cleanName}";

                try
                {
                    await SharedScraperLogic.DownloadWithProgressAsync(downloadUrl, fullFilePath, _url, cleanName, 1, _job, token);
                    _logger.LogInformation($"Downloaded file {count}/{total}: {finalFileName}");

                    if (!ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation($"Converting {finalFileName} to MP4 format...");
                        string newPath = await SharedScraperLogic.ConvertToMp4Async(fullFilePath, _job, token, _env);
                        _job.FinalFilePath = newPath;
                    }
                }
                catch (Exception ex)
                {
                    _job.Status = $"Failed file {count}: {ex.Message}";
                    await Task.Delay(2000, token);
                }

                AddURLToPendingFiles(cleanName, _url);
                count++;
            }
        }

        private void AddURLToPendingFiles(string fileName, string url)
        {
            _logger.LogInformation($"Adding URL to pending files: {url} for file: {fileName}");
            string pendingFilePath = Path.Combine(_env.WebRootPath, "Lookups", "PendingFileURLs.json");
            var pendingFiles = new PendingVideoUrlContainer();

            if (!File.Exists(pendingFilePath))
            {
                pendingFiles.Urls.Add(fileName, url);
            }
            else
            {
                pendingFiles = JsonHandler.DeserializeJsonFile<PendingVideoUrlContainer>(pendingFilePath);

                if (!pendingFiles.Urls.ContainsKey(fileName))
                {
                    pendingFiles.Urls.Add(fileName, url);
                }
            }

            JsonHandler.SerializeJsonFile(pendingFilePath, pendingFiles);
        }

        private bool IsVideo(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string cleanPath = path.Split('?')[0];
            string ext = Path.GetExtension(cleanPath).ToLower();
            return ext == ".mp4" || ext == ".m4v" || ext == ".mov" || ext == ".webm" || ext == ".mkv" || ext == ".m3u8";
        }
    }
}