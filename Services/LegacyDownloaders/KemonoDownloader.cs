using PuppeteerSharp;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Siphon.Services;
using System.Web;

namespace Siphon.Services.LegacyDownloaders
{
    public class KemonoDownloader
    {
        private string _downloadPath;
        private string _url;
        private DownloadJob _job;
        private string _sessionCookie;
        private readonly ILogger _logger;

        public KemonoDownloader(string path, string url, DownloadJob job, string sessionCookie, ILogger logger)
        {
            _downloadPath = path;
            _url = url;
            _job = job;
            _sessionCookie = sessionCookie;
            _logger = logger;
        }

        public async Task Download(CancellationToken token)
        {
            _job.Status = "Initializing Kemono Browser...";

            if (!_url.Contains("/post/"))
                throw new Exception("Invalid URL. Must be a specific post link.");

            JsonNode rootNode = null;
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
                            Name = "session",
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

                    string jsonContent = await page.EvaluateFunctionAsync<string>(@"async () => {
                        const apiPath = '/api/v1' + window.location.pathname;
                        const apiUrl = window.location.origin + apiPath;
                        
                        const response = await fetch(apiUrl);
                        if (!response.ok) throw new Error('API Error: ' + response.status);
                        return await response.text();
                    }");

                    if (string.IsNullOrWhiteSpace(jsonContent) || !jsonContent.Trim().StartsWith("{"))
                        throw new Exception("Invalid JSON content returned from in-page fetch.");

                    rootNode = JsonNode.Parse(jsonContent);
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

            if (rootNode == null) throw new Exception("API returned empty data.");

            // --- CHANGED LOGIC START ---
            var videosToDownload = new List<(string Path, string Name)>();

            var mainFile = rootNode["file"];
            if (mainFile != null && mainFile["path"] != null)
            {
                string path = mainFile["path"].ToString();
                string name = mainFile["name"]?.ToString();
                if (IsVideo(path)) videosToDownload.Add((path, name));
            }

            var attachments = rootNode["attachments"]?.AsArray();
            if (attachments != null)
            {
                foreach (var att in attachments)
                {
                    string path = att["path"]?.ToString();
                    string name = att["name"]?.ToString();

                    if (!string.IsNullOrEmpty(path) && IsVideo(path))
                    {
                        if (!videosToDownload.Any(v => v.Path == path))
                        {
                            videosToDownload.Add((path, name));
                        }
                    }
                }
            }
            // --- CHANGED LOGIC END ---

            if (videosToDownload.Count == 0) throw new Exception("No video files found.");

            int count = 1;
            int total = videosToDownload.Count;

            var baseUri = new Uri(_url);
            string downloadBase = $"{baseUri.Scheme}://{baseUri.Host}";

            foreach (var video in videosToDownload)
            {
                token.ThrowIfCancellationRequested();

                string downloadUrl = video.Path.StartsWith("http") ? video.Path : $"{downloadBase}{video.Path}";

                string rawFileName = video.Name;

                // Fallback to ?f= parameter
                if (string.IsNullOrWhiteSpace(rawFileName) && downloadUrl.Contains("?f="))
                {
                    var parts = downloadUrl.Split("?f=");
                    if (parts.Length > 1) rawFileName = parts[1];
                }

                // Fallback to URL path hash
                if (string.IsNullOrWhiteSpace(rawFileName))
                {
                    rawFileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
                }

                string ext = Path.GetExtension(rawFileName);
                if (string.IsNullOrEmpty(ext)) ext = ".mp4";

                string nameWithoutExt = Path.GetFileNameWithoutExtension(rawFileName);
                string cleanName = SharedScraperLogic.SanitizeFileName(nameWithoutExt);

                if (total > 1 && videosToDownload.Count(v => v.Name == video.Name) > 1)
                {
                    cleanName = $"{cleanName}_{count}";
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
                        // This handles the conversion and deletes the old file
                        string newPath = await SharedScraperLogic.ConvertToMp4Async(fullFilePath, _job, token);
                        _job.FinalFilePath = newPath;
                    }
                }
                catch (Exception ex)
                {
                    _job.Status = $"Failed file {count}: {ex.Message}";
                    await Task.Delay(2000, token);
                }
                count++;
            }
        }

        private bool IsVideo(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string cleanPath = path.Split('?')[0];
            string ext = Path.GetExtension(cleanPath).ToLower();
            return ext == ".mp4" || ext == ".m4v" || ext == ".mov" || ext == ".webm" || ext == ".mkv";
        }
    }
}