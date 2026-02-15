using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Siphon.Services.LookupServices
{
    public class KemonoAPI
    {
        private readonly ILogger<KemonoAPI> _logger;
        private readonly string _sessionCookie;
        private const string Domain = "kemono.cr";
        private const string ImageDomain = "img.kemono.cr";

        public KemonoAPI(ILogger<KemonoAPI> logger, string sessionCookie)
        {
            _logger = logger;
            _sessionCookie = sessionCookie;
        }

        public class PostResult
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string User { get; set; }
            public string Service { get; set; }
            public string ThumbnailUrl { get; set; }
            public string OriginalUrl { get; set; }
            public int AttachmentCount { get; set; }
            public bool HasVideo { get; set; }
            public DateTime Published { get; set; }
        }

        public async Task<List<PostResult>> FetchPostsAsync(string serviceType, string searchUser, int offset)
        {
            IBrowser browser = null;
            try
            {
                var browserFetcher = new BrowserFetcher();
                await browserFetcher.DownloadAsync();

                browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = true,
                    Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
                });

                await using var page = await browser.NewPageAsync();

                string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                await page.SetUserAgentAsync(userAgent);

                // Inject session cookie if available
                if (!string.IsNullOrWhiteSpace(_sessionCookie))
                {
                    await page.SetCookieAsync(new CookieParam
                    {
                        Name = "session",
                        Value = _sessionCookie,
                        Domain = $".{Domain}",
                        Path = "/",
                        Secure = true
                    });
                }

                // Navigate to homepage to prime cookies/Cloudflare
                //_logger.LogInformation($"Navigating to homepage of {Domain} to prime cookies...");
                await page.GoToAsync($"https://{Domain}", new NavigationOptions
                {
                    WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }
                });

                // Extract fresh cookies
                var browserCookies = await page.GetCookiesAsync($"https://{Domain}");

                // Build API URL
                string apiUrl = BuildApiUrl(serviceType, searchUser, offset);

                _logger.LogInformation($"Fetching Kemono API: {apiUrl}");

                // Setup HttpClient with cookies
                var handler = new HttpClientHandler
                {
                    CookieContainer = new CookieContainer(),
                    UseCookies = true,
                    AutomaticDecompression = DecompressionMethods.All
                };

                foreach (var c in browserCookies)
                {
                    handler.CookieContainer.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain));
                }

                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                client.DefaultRequestHeaders.Referrer = new Uri($"https://{Domain}/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/css"));

                var response = await client.GetAsync(apiUrl);
                string jsonContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Kemono API Request Failed: {response.StatusCode}. Response: {jsonContent}");
                    return new List<PostResult>();
                }

                return ParsePosts(jsonContent, serviceType, searchUser);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kemono Fetch Error: {ex.Message}");
                return new List<PostResult>();
            }
            finally
            {
                if (browser != null) await browser.CloseAsync();
            }
        }

        private string BuildApiUrl(string serviceType, string searchUser, int offset)
        {
            bool isUserFeed = !string.IsNullOrWhiteSpace(searchUser) && serviceType != "all";

            if (isUserFeed)
            {
                string url = $"https://{Domain}/api/v1/{serviceType}/user/{searchUser}/posts";
                if (offset > 0) url += $"?o={offset}";
                return url;
            }
            else
            {
                string url = $"https://{Domain}/api/v1/posts?o={offset}";
                if (!string.IsNullOrWhiteSpace(searchUser)) url += $"&q={searchUser}";
                return url;
            }
        }

        private List<PostResult> ParsePosts(string jsonContent, string serviceType, string searchUser)
        {
            JsonNode rootNode;
            try
            {
                rootNode = JsonNode.Parse(jsonContent);
            }
            catch
            {
                _logger.LogError("Failed to parse Kemono JSON.");
                return new List<PostResult>();
            }

            JsonArray postsArray = null;
            if (rootNode is JsonArray arr) postsArray = arr;
            else if (rootNode is JsonObject obj && obj.ContainsKey("results") && obj["results"] is JsonArray resultsArr)
                postsArray = resultsArr;
            else if (rootNode is JsonObject objPosts && objPosts.ContainsKey("posts") && objPosts["posts"] is JsonArray postsInner)
                postsArray = postsInner;

            if (postsArray == null)
            {
                _logger.LogWarning($"No valid post array found in Kemono response.");
                return new List<PostResult>();
            }

            var resultsBag = new ConcurrentBag<PostResult>();

            Parallel.ForEach(postsArray, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, node =>
            {
                try
                {
                    string service = node["service"]?.ToString() ?? serviceType;
                    string userId = node["user"]?.ToString() ?? searchUser;
                    string postId = node["id"]?.ToString();

                    var post = new PostResult
                    {
                        Id = postId,
                        Title = node["title"]?.ToString() ?? "Untitled",
                        User = userId,
                        Service = service,
                        Published = DateTime.TryParse(node["published"]?.ToString(), out var d) ? d : DateTime.Now,
                        AttachmentCount = node["attachments"]?.AsArray().Count ?? 0,
                        OriginalUrl = $"https://{Domain}/{service}/user/{userId}/post/{postId}"
                    };

                    string fPath = node["file"]?["path"]?.ToString();
                    bool videoInMain = IsVideo(fPath);
                    bool videoInAtt = false;

                    var atts = node["attachments"]?.AsArray();
                    if (atts != null)
                    {
                        foreach (var att in atts)
                        {
                            if (IsVideo(att["path"]?.ToString()))
                            {
                                videoInAtt = true;
                                break;
                            }
                        }
                    }

                    post.HasVideo = videoInMain || videoInAtt;
                    post.ThumbnailUrl = GetThumbnailFromNode(node);

                    resultsBag.Add(post);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse Kemono post: {ex.Message}");
                }
            });

            return resultsBag.OrderByDescending(x => x.Published).ToList();
        }

        private string GetThumbnailFromNode(JsonNode node)
        {
            string firstVideoUrl = null;

            var fileNode = node["file"];
            if (fileNode != null)
            {
                string path = fileNode["path"]?.ToString();
                if (IsImage(path)) return $"https://{ImageDomain}/thumbnail/data{path}";
                if (IsVideo(path) && firstVideoUrl == null) firstVideoUrl = $"https://{Domain}/data{path}";
            }

            var atts = node["attachments"]?.AsArray();
            if (atts != null)
            {
                foreach (var att in atts)
                {
                    string path = att["path"]?.ToString();
                    if (IsImage(path)) return $"https://{ImageDomain}/thumbnail/data{path}";
                    if (IsVideo(path) && firstVideoUrl == null) firstVideoUrl = $"https://{Domain}/data{path}";
                }
            }

            return firstVideoUrl;
        }

        private bool IsImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path.Split('?')[0]).ToLower();
            return new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" }.Contains(ext);
        }

        private bool IsVideo(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path.Split('?')[0]).ToLower();
            return new[] { ".mp4", ".mkv", ".webm", ".mov", ".m4v" }.Contains(ext);
        }

        public string GetSessionCookie() => _sessionCookie;
        public string GetDomain() => Domain;
    }
}