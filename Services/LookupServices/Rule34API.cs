using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

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
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 SiphonApp/1.0");
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

        public async Task<List<PostResult>> FetchPostsAsync(string tags, int offset)
        {
            try
            {
                int limit = 50;
                int pid = offset / limit;

                string tagQuery = string.IsNullOrWhiteSpace(tags) ? "index" : Uri.EscapeDataString(tags);
                string apiUrl = $"https://api.rule34.xxx/index.php?page=dapi&s=post&q=index&json=1&limit={limit}&pid={pid}&tags={tagQuery}";

                // Append API key and User ID if they exist in config
                if (!string.IsNullOrWhiteSpace(_userId) && !string.IsNullOrWhiteSpace(_apiKey))
                {
                    apiUrl += $"&user_id={_userId}&api_key={_apiKey}";
                }

                _logger.LogInformation($"Fetching Rule34 API: {apiUrl}");

                var response = await _httpClient.GetAsync(apiUrl);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Rule34 API Request Failed: {response.StatusCode}");
                    return new List<PostResult>();
                }
                if (await response.Content.ReadAsStringAsync() == "\"Missing authentication. Go to api.rule34.xxx for more information\"")
                {
                    return new List<PostResult>() { new PostResult() { Id = "Rule34 Not Authed" } };
                }

                string jsonContent = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(jsonContent) || jsonContent == "[]")
                {
                    return new List<PostResult>() { new PostResult { Id = "End Of Posts" } };
                }

                var rootNode = JsonNode.Parse(jsonContent);
                var postsArray = rootNode as JsonArray;

                if (postsArray == null) return new List<PostResult>();

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
                            // Send the raw video URL to the thumbnail handler if it's a video, otherwise use the preview URL
                            ThumbnailUrl = hasVideo ? fileUrl : (node["preview_url"]?.ToString() ?? fileUrl)
                        };

                        // Approximate published date from unix timestamp if available (change field)
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
                        _logger.LogWarning($"Failed to parse Rule34 post: {ex.Message}");
                    }
                });

                return resultsBag.OrderByDescending(x => x.Published).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Rule34 Fetch Error: {ex.Message}");
                return new List<PostResult>();
            }
        }
    }
}