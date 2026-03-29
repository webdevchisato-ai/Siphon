using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Siphon.Services.LookupServices
{
    public class Rule34API
    {
        private readonly ILogger<Rule34API> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _userId;
        private readonly string _apiKey;

        public Rule34API(ILogger<Rule34API> logger, string userId, string apiKey)
        {
            _logger = logger;
            _userId = userId;
            _apiKey = apiKey;

            // Hooking into the local Tor SOCKS5 proxy for the HttpClient API attempts
            var proxy = new WebProxy("socks5://127.0.0.1:9050");
            var handler = new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            _logger.LogInformation("[Rule34API] Initialized with UserID: {UserId}, ApiKey Provided: {HasKey}. Routing via Tor SOCKS5 Proxy (127.0.0.1:9050).",
                string.IsNullOrWhiteSpace(userId) ? "None" : userId,
                !string.IsNullOrWhiteSpace(apiKey));
        }

        public class PostResult
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string User { get; set; }
            public string Service { get; set; }
            public string ThumbnailUrl { get; set; }
            public string FirstVideoUrl { get; set; }
            public string OriginalUrl { get; set; }
            public int AttachmentCount { get; set; }
            public bool HasVideo { get; set; }
            public double VideoDuration { get; set; } = 0;
            public DateTime Published { get; set; }
        }

        public async Task<List<PostResult>> FetchPostsAsync(string tags, int offset)
        {
            try
            {
                int limit = 50;
                string tagQuery = string.IsNullOrWhiteSpace(tags) ? "index" : Uri.EscapeDataString(tags);
                bool attemptAuth = !string.IsNullOrWhiteSpace(_userId) && !string.IsNullOrWhiteSpace(_apiKey);

                // --- 1. AUTHENTICATED API ROUTE (HttpClient) ---
                if (attemptAuth)
                {
                    int pid = offset / limit;
                    string apiUrl = $"https://api.rule34.xxx/index.php?page=dapi&s=post&q=index&json=1&limit={limit}&pid={pid}&tags={tagQuery}&user_id={_userId}&api_key={_apiKey}";

                    _logger.LogInformation($"[Rule34API] Attempting authenticated API fetch via Tor for tags: '{tags}', Offset: {offset}");
                    var response = await _httpClient.GetAsync(apiUrl);

                    if (response.IsSuccessStatusCode)
                    {
                        string jsonContent = await response.Content.ReadAsStringAsync();
                        if (!jsonContent.Contains("Missing authentication"))
                        {
                            return ParseJsonPayload(jsonContent);
                        }
                        _logger.LogWarning("[Rule34API] Auth error detected in API payload. Keys may be invalid. Falling back to Puppeteer scraper.");
                    }
                    else if ((int)response.StatusCode == 403 || (int)response.StatusCode == 451)
                    {
                        _logger.LogWarning($"[Rule34API] HTTP {response.StatusCode} on API route (Cloudflare/Region Block). Falling back to Puppeteer scraper.");
                    }
                    else
                    {
                        _logger.LogWarning($"[Rule34API] API request failed with HTTP {response.StatusCode}. Falling back to Puppeteer scraper.");
                    }
                }

                // --- 2. UNAUTHENTICATED HTML SCRAPER ROUTE (Puppeteer via Tor) ---
                _logger.LogInformation($"[Rule34API] Using Unauthenticated Puppeteer Scraper via Tor for tags: '{tags}', Offset: {offset}");
                string htmlUrl = $"https://rule34.xxx/index.php?page=post&s=list&tags={tagQuery}&pid={offset}";

                return await FetchHtmlViaPuppeteerAsync(htmlUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Rule34API] Critical Fetch Error: {ex.Message}\n{ex.StackTrace}");
                return new List<PostResult>();
            }
        }

        private async Task<List<PostResult>> FetchHtmlViaPuppeteerAsync(string url)
        {
            int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                IBrowser browser = null;
                try
                {
                    await new BrowserFetcher().DownloadAsync();

                    browser = await Puppeteer.LaunchAsync(new LaunchOptions
                    {
                        Headless = true,
                        Args = new[] { "--no-sandbox", "--proxy-server=socks5://127.0.0.1:9050" }
                    });

                    var page = await browser.NewPageAsync();
                    await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    _logger.LogInformation($"[Rule34API] Attempt {attempt}: Puppeteer navigating to: {url}");

                    // Using DOMContentLoaded just like your working downloader
                    await page.GoToAsync(url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }, Timeout = 30000 });

                    // Check for Cloudflare challenge
                    var pageTitle = await page.GetTitleAsync();
                    if (pageTitle != null && pageTitle.Contains("Just a moment"))
                    {
                        _logger.LogWarning($"[Rule34API] Attempt {attempt}: Cloudflare challenge detected. Waiting up to 30s for resolution...");
                        try
                        {
                            // Wait for the main content container to load, indicating CF passed
                            await page.WaitForSelectorAsync("#content", new WaitForSelectorOptions { Timeout = 30000 });
                            _logger.LogInformation($"[Rule34API] Attempt {attempt}: Cloudflare challenge bypassed successfully.");
                        }
                        catch (Exception)
                        {
                            throw new Exception("Cloudflare challenge did not resolve. Tor exit node may be hard-blocked.");
                        }
                    }

                    string htmlContent = await page.GetContentAsync();

                    if (htmlContent.Contains("Nobody here but us chickens!"))
                    {
                        _logger.LogInformation("[Rule34API] End of posts reached (HTML returned empty state).");
                        return new List<PostResult>() { new PostResult { Id = "End Of Posts" } };
                    }

                    var parsedPosts = ParseHtmlPayload(htmlContent);

                    if (parsedPosts.Count > 0)
                    {
                        return parsedPosts;
                    }
                    else if (htmlContent.Contains("thumb"))
                    {
                        _logger.LogWarning("[Rule34API] Page loaded but regex failed to match. HTML structure may have changed.");
                        return new List<PostResult>();
                    }
                    else
                    {
                        throw new Exception("Failed to find post grid in HTML. Cloudflare may still be blocking the page content.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Rule34API] Attempt {attempt} Failed: {ex.Message}");
                    if (attempt == maxRetries)
                    {
                        _logger.LogError($"[Rule34API] Max retries reached. Puppeteer Scraper failed.");
                        return new List<PostResult>();
                    }
                    await Task.Delay(2000); // Short delay before retry
                }
                finally
                {
                    if (browser != null && !browser.IsClosed)
                    {
                        await browser.CloseAsync();
                    }
                }
            }
            return new List<PostResult>();
        }

        private List<PostResult> ParseJsonPayload(string jsonContent)
        {
            if (string.IsNullOrWhiteSpace(jsonContent) || jsonContent == "[]")
            {
                return new List<PostResult>() { new PostResult { Id = "End Of Posts" } };
            }

            JsonArray postsArray;
            try
            {
                var rootNode = JsonNode.Parse(jsonContent);
                postsArray = rootNode as JsonArray;
            }
            catch (Exception parseEx)
            {
                _logger.LogWarning($"[Rule34API] Exception parsing JSON: {parseEx.Message}. Raw data: {jsonContent}");
                return new List<PostResult>();
            }

            if (postsArray == null) return new List<PostResult>();

            _logger.LogInformation($"[Rule34API] Successfully parsed {postsArray.Count} raw posts via JSON API.");
            var resultsBag = new ConcurrentBag<PostResult>();

            Parallel.ForEach(postsArray, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, node =>
            {
                try
                {
                    string id = node["id"]?.ToString();
                    string fileUrl = node["file_url"]?.ToString() ?? "";
                    string tagsList = node["tags"]?.ToString() ?? "Untitled";
                    bool hasVideo = fileUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || fileUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);

                    var post = new PostResult
                    {
                        Id = id,
                        Title = tagsList.Length > 50 ? tagsList.Substring(0, 47) + "..." : tagsList,
                        User = node["owner"]?.ToString() ?? "Rule34",
                        Service = "rule34",
                        OriginalUrl = $"https://rule34.xxx/index.php?page=post&s=view&id={id}",
                        AttachmentCount = 1,
                        HasVideo = hasVideo,
                        FirstVideoUrl = hasVideo ? fileUrl : null,
                        ThumbnailUrl = node["preview_url"]?.ToString() ?? fileUrl,
                        VideoDuration = 0
                    };

                    if (long.TryParse(node["change"]?.ToString(), out long unixTime))
                    {
                        post.Published = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                    }
                    else
                    {
                        post.Published = DateTime.Now;
                    }

                    resultsBag.Add(post);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Rule34API] Failed to parse individual JSON post. Error: {ex.Message}");
                }
            });

            return resultsBag.OrderByDescending(x => x.Published).ToList();
        }

        private List<PostResult> ParseHtmlPayload(string htmlContent)
        {
            var regex = new Regex(@"<span class=""thumb"" id=""s(\d+)"">.*?<img src=""([^""]+)""[^>]*?title=""([^""]+)""", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var matches = regex.Matches(htmlContent);

            if (matches.Count == 0)
            {
                _logger.LogWarning("[Rule34API] HTML Scraper found 0 posts. Regex may need updating or end of feed reached.");
                return new List<PostResult>();
            }

            _logger.LogInformation($"[Rule34API] HTML Scraper successfully extracted {matches.Count} raw posts.");
            var resultsBag = new ConcurrentBag<PostResult>();

            Parallel.ForEach(matches.Cast<Match>(), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, match =>
            {
                try
                {
                    string id = match.Groups[1].Value;
                    string thumbnailUrl = match.Groups[2].Value;
                    string tagsList = System.Web.HttpUtility.HtmlDecode(match.Groups[3].Value);

                    bool hasVideo = tagsList.Contains("video", StringComparison.OrdinalIgnoreCase) ||
                                    tagsList.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
                                    tagsList.Contains("webm", StringComparison.OrdinalIgnoreCase);

                    string fileUrl = thumbnailUrl;
                    var thumbMatch = Regex.Match(thumbnailUrl, @"thumbnails/(\d+)/thumbnail_([a-f0-9]+)\.");

                    if (thumbMatch.Success)
                    {
                        string dir = thumbMatch.Groups[1].Value;
                        string hash = thumbMatch.Groups[2].Value;

                        if (hasVideo)
                        {
                            string ext = tagsList.Contains("webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".mp4";
                            fileUrl = $"https://wimg.rule34.xxx//images/{dir}/{hash}{ext}";
                        }
                        else
                        {
                            string ext = tagsList.Contains("gif", StringComparison.OrdinalIgnoreCase) ? ".gif" :
                                         tagsList.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                            fileUrl = $"https://wimg.rule34.xxx//images/{dir}/{hash}{ext}";
                        }
                    }

                    var post = new PostResult
                    {
                        Id = id,
                        Title = tagsList.Length > 50 ? tagsList.Substring(0, 47) + "..." : tagsList,
                        User = "Rule34",
                        Service = "rule34",
                        OriginalUrl = $"https://rule34.xxx/index.php?page=post&s=view&id={id}",
                        AttachmentCount = 1,
                        HasVideo = hasVideo,
                        FirstVideoUrl = hasVideo ? fileUrl : null,
                        ThumbnailUrl = thumbnailUrl,
                        VideoDuration = 0,
                        Published = DateTime.Now
                    };

                    resultsBag.Add(post);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Rule34API] Failed to parse HTML post. Error: {ex.Message}");
                }
            });

            return resultsBag.ToList();
        }
    }
}