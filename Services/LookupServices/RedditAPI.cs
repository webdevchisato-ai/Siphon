using Microsoft.Extensions.Logging;
using Siphon.Services.LegacyDownloaders.Video;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace Siphon.Services.LookupServices
{
    public class RedditAPI
    {
        private readonly ILogger<RedditAPI> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _redditCookie;

        // Cache to bridge integer offsets to Reddit's cursor-based 'after' tokens
        private static readonly ConcurrentDictionary<string, string> _paginationCache = new ConcurrentDictionary<string, string>();

        public RedditAPI(ILogger<RedditAPI> logger, string redditCookie)
        {
            _logger = logger;
            _redditCookie = redditCookie;

            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            if (!string.IsNullOrWhiteSpace(_redditCookie))
            {
                handler.CookieContainer.Add(new Cookie("reddit_session", _redditCookie, "/", ".reddit.com"));
            }

            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 SiphonApp/1.0");
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
            public string PageAfterToken { get; set; } // <--- Added to track pagination state
        }

        // Method for the main model to inject a recovered token from the JSON cache
        public static void SetPaginationToken(string subreddit, int nextOffset, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            string subName = string.IsNullOrWhiteSpace(subreddit) ? "all" : Uri.EscapeDataString(subreddit);
            _paginationCache[$"{subName}_{nextOffset}"] = token;
        }

        public async Task<List<PostResult>> FetchPostsAsync(string subreddit, int offset)
        {
            try
            {
                int limit = 50;
                string subName = string.IsNullOrWhiteSpace(subreddit) ? "all" : Uri.EscapeDataString(subreddit);

                string apiUrl = $"https://www.reddit.com/r/{subName}/new.json?limit={limit}&count={offset}";

                // Inject the 'after' token if we have it for this offset
                if (offset > 0)
                {
                    string cacheKey = $"{subName}_{offset}";
                    if (_paginationCache.TryGetValue(cacheKey, out string afterToken))
                    {
                        apiUrl += $"&after={afterToken}";
                    }
                    else
                    {
                        _logger.LogWarning($"Missing Reddit pagination token for offset {offset}. Results might loop.");
                    }
                }
                else
                {
                    // Clear out old cache if we are starting fresh at offset 0
                    var keysToRemove = _paginationCache.Keys.Where(k => k.StartsWith($"{subName}_")).ToList();
                    foreach (var key in keysToRemove)
                    {
                        _paginationCache.TryRemove(key, out _);
                    }
                }

                _logger.LogInformation($"Fetching Reddit API: {apiUrl}");

                var response = await _httpClient.GetAsync(apiUrl);
                string jsonContent = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning($"Reddit Auth Error ({response.StatusCode}). Cookie might be expired.");
                    return new List<PostResult>() { new PostResult() { Id = "Reddit Not Authed" } };
                }

                if (response.StatusCode == HttpStatusCode.NotFound || jsonContent.Contains("\"error\": 404"))
                {
                    _logger.LogWarning($"Reddit Subreddit Not Found: {subName}");
                    return new List<PostResult>() { new PostResult() { Id = "Not Found" } };
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Reddit API Request Failed: {response.StatusCode}");
                    return new List<PostResult>();
                }

                if (string.IsNullOrWhiteSpace(jsonContent) || jsonContent == "{}")
                {
                    return new List<PostResult>() { new PostResult { Id = "End Of Posts" } };
                }

                var rootNode = JsonNode.Parse(jsonContent);
                var postsArray = rootNode?["data"]?["children"] as JsonArray;

                // Grab the 'after' token for the next page and store it based on the expected next offset
                string nextAfter = rootNode?["data"]?["after"]?.ToString();
                if (!string.IsNullOrWhiteSpace(nextAfter))
                {
                    _paginationCache[$"{subName}_{offset + limit}"] = nextAfter;
                }

                if (postsArray == null || postsArray.Count == 0)
                {
                    return new List<PostResult>() { new PostResult { Id = "End Of Posts" } };
                }

                var resultsBag = new ConcurrentBag<PostResult>();

                var tasks = postsArray.Select(async node =>
                {
                    try
                    {
                        var data = node["data"];
                        if (data == null) return;

                        string id = data["name"]?.ToString();
                        string title = data["title"]?.ToString() ?? "Untitled";
                        string permalink = data["permalink"]?.ToString() ?? "";

                        string fileUrl = "";
                        bool hasVideo = false;
                        double duration = 0;

                        if (data["is_video"]?.GetValue<bool>() == true)
                        {
                            fileUrl = data["media"]?["reddit_video"]?["fallback_url"]?.ToString() ?? "";
                            hasVideo = true;

                            if (double.TryParse(data["media"]?["reddit_video"]?["duration"]?.ToString(), out double d))
                            {
                                duration = d;
                            }
                        }
                        else if (data["url"] != null)
                        {
                            string externalUrl = data["url"].ToString();
                            if (externalUrl.Contains("redgifs.com"))
                            {
                                fileUrl = externalUrl;
                                hasVideo = true;

                                var redGifsData = await SharedScraperLogic.ResolveRedGifsUrlWithDurationAsync(externalUrl, CancellationToken.None);
                                duration = redGifsData.Duration;
                            }
                            else if (externalUrl.EndsWith(".mp4") || externalUrl.EndsWith(".webm") || externalUrl.EndsWith(".gifv"))
                            {
                                fileUrl = externalUrl;
                                hasVideo = true;
                            }
                            else if (externalUrl.EndsWith(".jpg") || externalUrl.EndsWith(".png") || externalUrl.EndsWith(".jpeg") || externalUrl.EndsWith(".gif"))
                            {
                                fileUrl = externalUrl;
                            }
                        }

                        string thumbnailUrl = "";
                        var previewNode = data["preview"]?["images"]?[0]?["source"]?["url"];
                        if (previewNode != null)
                        {
                            thumbnailUrl = System.Web.HttpUtility.HtmlDecode(previewNode.ToString());
                        }
                        else
                        {
                            thumbnailUrl = System.Web.HttpUtility.HtmlDecode(data["thumbnail"]?.ToString() ?? "");
                        }

                        if (string.IsNullOrWhiteSpace(thumbnailUrl) || thumbnailUrl == "self" || thumbnailUrl == "default" || thumbnailUrl == "nsfw" || thumbnailUrl == "spoiler")
                        {
                            thumbnailUrl = System.Web.HttpUtility.HtmlDecode(fileUrl);
                        }

                        var post = new PostResult
                        {
                            Id = id,
                            Title = title,
                            User = data["author"]?.ToString() ?? "Unknown",
                            Service = "reddit",
                            OriginalUrl = $"https://www.reddit.com{permalink}",
                            AttachmentCount = 1,
                            HasVideo = hasVideo,
                            VideoDuration = duration,
                            FirstVideoUrl = hasVideo ? fileUrl : null,
                            ThumbnailUrl = thumbnailUrl,
                            PageAfterToken = nextAfter // <--- Attach token to the post to be cached
                        };

                        if (long.TryParse(data["created_utc"]?.ToString(), out long unixTime))
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
                        _logger.LogWarning($"Failed to parse Reddit post: {ex.Message}");
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                return resultsBag.OrderByDescending(x => x.Published).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Reddit Fetch Error: {ex.Message}");
                return new List<PostResult>();
            }
        }
    }
}