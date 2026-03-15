using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Siphon.Services;
using Siphon.Services.LookupServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace Siphon.Pages
{
    public class ExternalIntegrationsModel : PageModel
    {
        private readonly ILogger<ExternalIntegrationsModel> _logger;
        private readonly ILogger<KemonoAPI> _kemonoLogger;
        private readonly ILogger<CoomerAPI> _coomerLogger;
        private readonly ILogger<Rule34API> _rule34Logger;
        private readonly DownloadManager _downloadManager;
        private readonly IWebHostEnvironment _env;
        private readonly ICompositeViewEngine _viewEngine;
        private readonly ITempDataProvider _tempDataProvider;
        private readonly string _configPath;
        [BindProperty] public string _kemonoSession { get; set; } = "";
        [BindProperty] public string _coomerSession { get; set; } = "";
        [BindProperty] public string _rule34UserId { get; set; } = "";
        [BindProperty] public string _rule34ApiKey { get; set; } = "";
        [BindProperty] public string _phpssessid { get; set; } = "";
        [BindProperty] public string _eprns { get; set; } = "";
        [BindProperty] public string _cfClearence { get; set; } = "";
        [BindProperty] public string _mangaViewCookie { get; set; } = "";
        [BindProperty] public string _hentaiDudeAgent { get; set; } = "";
        [BindProperty] public string _redditCookie { get; set; } = "";
        public int DOWNLOADERThreads { get; set; }

        // Limit concurrent FFmpeg operations
        private static readonly SemaphoreSlim _thumbGenerationLock = new SemaphoreSlim(3);

        // Cache for video durations to avoid repeated ffprobe calls
        private static readonly ConcurrentDictionary<string, double> _durationCache = new ConcurrentDictionary<string, double>();

        public ExternalIntegrationsModel(
         ILogger<ExternalIntegrationsModel> logger,
         ILogger<KemonoAPI> kemonoLogger,
         ILogger<CoomerAPI> coomerLogger,
         ILogger<Rule34API> rule34Logger,
         DownloadManager downloadManager,
         IWebHostEnvironment environment,
         ICompositeViewEngine viewEngine,
         ITempDataProvider tempDataProvider)
        {
            _logger = logger;
            _kemonoLogger = kemonoLogger;
            _coomerLogger = coomerLogger;
            _rule34Logger = rule34Logger;
            _downloadManager = downloadManager;
            _env = environment;
            _viewEngine = viewEngine;
            _tempDataProvider = tempDataProvider;
            _configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "scraper_config.txt");
            LoadConfig();
        }

        public List<ExternalPost> Posts { get; set; } = new();
        public List<ExternalCreator> Creators { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public string ViewMode { get; set; } = "artists";

        [BindProperty(SupportsGet = true)]
        public string ArtistSortMode { get; set; } = "popularity"; // updated, popularity, indexed, alphabetical, service

        [BindProperty(SupportsGet = true)]
        public bool ReturnToArtists { get; set; } = false;

        [BindProperty(SupportsGet = true)]
        public int ArtistOffset { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public string Site { get; set; } = "kemono.cr";

        [BindProperty(SupportsGet = true)]
        public string ServiceType { get; set; } = "all";

        [BindProperty(SupportsGet = true)]
        public string SearchUser { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool OnlyVideos { get; set; } = false;

        [BindProperty(SupportsGet = true)]
        public int MinAttachments { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public int Offset { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public bool SearchTriggered { get; set; } = false;

        [BindProperty(SupportsGet = true)]
        public bool EnableVideoLengthFilter { get; set; } = false;

        [BindProperty(SupportsGet = true)]
        public int MinVideoLength { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public int MaxVideoLength { get; set; } = 10000;

        // New property to flag timeouts
        public bool IsSearchTimeout { get; set; } = false;

        [BindProperty]
        public string TimeOutMessage { get; set; } = "";

        [BindProperty]
        public string TimeOutHeader { get; set; } = "";

        [BindProperty]
        public TimeOutType timeOutType { get; set; }

        [BindProperty]
        public string foundFilesMessage { get; set; } = "";

        public enum TimeOutType
        {
            EndOfPosts,
            NotFound,
            ApiIssue,
            StandardTimeout,
        }

        public class ExternalPost
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
            public double VideoDuration { get; set; } = 0; // Duration in seconds
        }

        public class ExternalCreator
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Service { get; set; }
            public string ProfileIconUrl { get; set; }
            public DateTime Updated { get; set; }
            public DateTime Indexed { get; set; }
            public int Favorited { get; set; }
            public string OriginalUrl { get; set; }
        }

        public void OnGet()
        {
            // Just initialize defaults. 
        }

        public async Task<IActionResult> OnGetStreamSearchAsync()
        {
            Response.ContentType = "text/event-stream";

            // Helper to write events
            async Task SendEvent(string type, string payload)
            {
                var json = JsonSerializer.Serialize(new { type, payload });
                await Response.WriteAsync($"data: {json}\n\n");
                await Response.Body.FlushAsync();
            }

            TimeOutHeader = "";
            TimeOutMessage = "";
            foundFilesMessage = "";

            string cacheFolder = Path.Combine(_env.WebRootPath, "ExternalIntegrationCache");
            if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

            // --- ARTISTS LOOKUP MODE ---
            if (ViewMode == "artists")
            {
                string cacheFileName = $"Feed_{Site}_Creators.json";
                string cachePath = Path.Combine(cacheFolder, cacheFileName);
                List<ExternalCreator> rawCreators = null;

                if (System.IO.File.Exists(cachePath) && System.IO.File.GetLastWriteTime(cachePath) > DateTime.Now.AddDays(-1))
                {
                    try
                    {
                        await SendEvent("status", "Checking local cache for artists...");
                        string json = await System.IO.File.ReadAllTextAsync(cachePath);
                        rawCreators = JsonSerializer.Deserialize<List<ExternalCreator>>(json);
                        if (rawCreators != null) SearchTriggered = true;
                    }
                    catch { }
                }

                if (rawCreators == null)
                {
                    if (!SearchTriggered) return new EmptyResult();
                    await SendEvent("status", "Contacting API for artists list...");

                    var totalTimeout = TimeSpan.FromMinutes(3);
                    var fetchTask = FetchCreatorsFromApi(Site);
                    var completedFetch = await Task.WhenAny(fetchTask, Task.Delay(totalTimeout));

                    if (completedFetch != fetchTask)
                    {
                        IsSearchTimeout = true;
                        timeOutType = TimeOutType.StandardTimeout;
                        _logger.LogWarning("Search timed out during API fetch phase.");
                        await SendEvent("error", "Search timed out contacting API.");
                        return new EmptyResult();
                    }
                    else
                    {
                        rawCreators = await fetchTask;
                        if (rawCreators != null && rawCreators.Count > 0)
                        {
                            string json = JsonSerializer.Serialize(rawCreators, new JsonSerializerOptions { WriteIndented = true });
                            await System.IO.File.WriteAllTextAsync(cachePath, json);
                        }
                        else
                        {
                            await SendEvent("error", "API returned empty data.");
                            return new EmptyResult();
                        }
                    }
                }

                if (rawCreators == null) rawCreators = new List<ExternalCreator>();

                var filteredCreators = rawCreators.AsEnumerable();

                if (ServiceType != "all")
                {
                    filteredCreators = filteredCreators.Where(c => string.Equals(c.Service, ServiceType, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(SearchUser))
                {
                    filteredCreators = filteredCreators.Where(c =>
                     (c.Name != null && c.Name.Contains(SearchUser, StringComparison.OrdinalIgnoreCase)) ||
                     (c.Id != null && c.Id.Contains(SearchUser, StringComparison.OrdinalIgnoreCase)));
                }

                // Apply Sorting
                filteredCreators = ArtistSortMode switch
                {
                    "popularity" => filteredCreators.OrderByDescending(c => c.Favorited),
                    "indexed" => filteredCreators.OrderByDescending(c => c.Indexed),
                    "alphabetical" => filteredCreators.OrderBy(c => c.Name),
                    "service" => filteredCreators.OrderBy(c => c.Service).ThenBy(c => c.Name),
                    _ => filteredCreators.OrderByDescending(c => c.Updated)
                };

                Creators = filteredCreators.Skip(Offset).Take(50).ToList();

                int nextOffset = Offset + 50;
                await SendEvent("metaOffset", JsonSerializer.Serialize(new { nextOffset = nextOffset }));
                await SendEvent("status", "Rendering results...");

                // Pass "this" model entirely to partial
                string htmlContent = await RenderPartialToStringAsync("_PostGrid", this);
                await SendEvent("result", htmlContent);
                return new EmptyResult();
            }

            // --- POSTS LOOKUP MODE ---
            int retryCount = 0;
            int maxRetries = 20; // Prevent infinite loops. Stop after checking 20 pages (1000 posts).
            bool keepSearching = true;

            while (keepSearching)
            {
                string scope = string.IsNullOrWhiteSpace(SearchUser) ? "Global" : $"User_{SearchUser}";
                if (ServiceType != "all") scope += $"_{ServiceType}";

                string cacheFileName = $"Feed_{Site}_{scope}_Offset{Offset}.json";
                string cachePath = Path.Combine(cacheFolder, cacheFileName);

                List<ExternalPost> rawPosts = null;

                if (System.IO.File.Exists(cachePath) && System.IO.File.GetLastWriteTime(cachePath) > DateTime.Now.AddDays(-1))
                {
                    try
                    {
                        await SendEvent("status", $"Checking local cache (Offset {Offset})...");
                        string json = await System.IO.File.ReadAllTextAsync(cachePath);
                        rawPosts = JsonSerializer.Deserialize<List<ExternalPost>>(json);
                        if (rawPosts != null) SearchTriggered = true;
                    }
                    catch { }
                }

                if (rawPosts == null)
                {
                    if (!SearchTriggered) return new EmptyResult();

                    await SendEvent("status", $"Contacting API (Offset {Offset})...");

                    var totalTimeout = TimeSpan.FromMinutes(3);
                    var stopwatch = Stopwatch.StartNew();
                    var fetchTask = FetchFromApi(Site, SearchUser, Offset);
                    var completedFetch = await Task.WhenAny(fetchTask, Task.Delay(totalTimeout));

                    if (completedFetch != fetchTask)
                    {
                        IsSearchTimeout = true;
                        timeOutType = TimeOutType.StandardTimeout;
                        _logger.LogWarning("Search timed out during API fetch phase.");
                        await SendEvent("error", "Search timed out contacting API.");
                        return new EmptyResult();
                    }
                    else
                    {
                        rawPosts = await fetchTask;

                        if (rawPosts.Count() > 0)
                        {
                            if (rawPosts[0].Id != null && rawPosts[0].Id.Contains("Not Found"))
                            {
                                IsSearchTimeout = true;
                                timeOutType = TimeOutType.NotFound;
                                await SendEvent("warning", "User not found. Please check the ID and Service.");
                                return new EmptyResult();
                            }
                            else if (rawPosts[0].Id != null && rawPosts[0].Id.Contains("End Of Posts"))
                            {
                                timeOutType = TimeOutType.EndOfPosts;
                                IsSearchTimeout = true;
                                await SendEvent("warning", "Reached the end of available posts.");
                                return new EmptyResult();
                            }
                            else if (rawPosts[0].Id == "Rule34 Not Authed")
                            {
                                IsSearchTimeout = true;
                                timeOutType = TimeOutType.ApiIssue;
                                await SendEvent("error", "API Auth Key not authenticated");
                                return new EmptyResult();
                            }

                            if (rawPosts != null && rawPosts.Count > 0)
                            {
                                var elapsed = stopwatch.Elapsed;
                                var remaining = totalTimeout - elapsed;

                                if (remaining > TimeSpan.Zero)
                                {
                                    await SendEvent("status", $"Found {rawPosts.Count} raw posts. Enriching metadata...");
                                    var enrichTask = EnrichWithVideoDurations(rawPosts, (msg) => SendEvent("status", msg));
                                    var completedEnrich = await Task.WhenAny(enrichTask, Task.Delay(remaining));

                                    if (completedEnrich != enrichTask)
                                    {
                                        IsSearchTimeout = true;
                                        _logger.LogWarning("Search timed out during Video Enrichment phase.");
                                        await SendEvent("warning", "Timeout during enrichment. Some metadata may be missing.");
                                    }
                                }
                                else
                                {
                                    IsSearchTimeout = true;
                                }

                                if (!IsSearchTimeout)
                                {
                                    string json = JsonSerializer.Serialize(rawPosts, new JsonSerializerOptions { WriteIndented = true });
                                    await System.IO.File.WriteAllTextAsync(cachePath, json);
                                }
                            }
                        }
                        else
                        {
                            IsSearchTimeout = true;
                            timeOutType = TimeOutType.ApiIssue;
                            await SendEvent("error", "API returned unknown or empty data.");
                            return new EmptyResult();
                        }
                    }
                }
                else
                {
                    await SendEvent("status", $"Found {rawPosts.Count} Cached Posts. Processing...");

                    var postsNeedingDuration = rawPosts.Where(p => p.HasVideo && p.VideoDuration == 0).ToList();
                    if (postsNeedingDuration.Any())
                    {
                        await SendEvent("status", $"Enriching {postsNeedingDuration.Count} cached video items...");
                        var enrichTask = EnrichWithVideoDurations(postsNeedingDuration, (msg) => SendEvent("status", msg));
                        var completed = await Task.WhenAny(enrichTask, Task.Delay(TimeSpan.FromMinutes(3)));

                        if (completed != enrichTask) IsSearchTimeout = true;

                        if (!IsSearchTimeout)
                        {
                            string json = JsonSerializer.Serialize(rawPosts, new JsonSerializerOptions { WriteIndented = true });
                            await System.IO.File.WriteAllTextAsync(cachePath, json);
                        }
                    }
                }

                if (rawPosts == null) rawPosts = new List<ExternalPost>();

                var filtered = rawPosts.AsEnumerable();

                if (OnlyVideos) filtered = filtered.Where(p => p.HasVideo);

                bool shouldRetry = false;

                if (OnlyVideos && filtered.Count() == 0)
                {
                    Offset += 50;
                    retryCount++;

                    if (retryCount < maxRetries)
                    {
                        shouldRetry = true;
                        await SendEvent("status", $"Page contained no videos. Scanning next page (Attempt {retryCount}/{maxRetries})...");
                    }
                    else
                    {
                        await SendEvent("warning", $"Scanned {maxRetries} pages without finding a video. Stopping search.");
                        return new EmptyResult();
                    }
                }

                if (!shouldRetry)
                {
                    keepSearching = false;

                    if (MinAttachments > 0) filtered = filtered.Where(p => p.AttachmentCount >= MinAttachments);

                    if (EnableVideoLengthFilter)
                    {
                        filtered = filtered.Where(p =>
                         !p.HasVideo ||
                         (p.VideoDuration >= MinVideoLength && p.VideoDuration <= MaxVideoLength)
                        );
                    }

                    Posts = filtered.ToList();
                }
            }

            int correctNextOffset = Offset + 50;
            await SendEvent("metaOffset", JsonSerializer.Serialize(new { nextOffset = correctNextOffset }));

            await SendEvent("status", "Rendering results...");

            string html = await RenderPartialToStringAsync("_PostGrid", this);

            await SendEvent("result", html);

            return new EmptyResult();
        }

        private async Task<string> RenderPartialToStringAsync(string viewName, object model)
        {
            var actionContext = new ActionContext(HttpContext, RouteData, PageContext.ActionDescriptor);

            using (var sw = new StringWriter())
            {
                var viewResult = _viewEngine.FindView(actionContext, viewName, false);
                if (viewResult.View == null) return string.Empty;

                var viewDictionary = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
                {
                    Model = model
                };

                var viewContext = new ViewContext(
                 actionContext,
                 viewResult.View,
                 viewDictionary,
                 new TempDataDictionary(actionContext.HttpContext, _tempDataProvider),
                 sw,
                 new HtmlHelperOptions()
                );

                await viewResult.View.RenderAsync(viewContext);
                return sw.ToString();
            }
        }

        private async Task EnrichWithVideoDurations(List<ExternalPost> posts, Func<string, Task> progressCallback = null)
        {
            var videoPosts = posts.Where(p => p.HasVideo && !string.IsNullOrEmpty(p.ThumbnailUrl)).ToList();

            if (!videoPosts.Any()) return;

            _logger.LogInformation($"Enriching {videoPosts.Count} video posts with duration data...");

            if (progressCallback != null)
                await progressCallback($"Found {videoPosts.Count} posts with videos. Processing...");

            int total = videoPosts.Count;
            int processed = 0;

            var tasks = videoPosts.Select(async post =>
            {
                try
                {
                    string videoUrl = post.ThumbnailUrl;

                    if (_durationCache.TryGetValue(videoUrl, out double cachedDuration))
                    {
                        post.VideoDuration = cachedDuration;
                    }
                    else
                    {
                        string domain = videoUrl.Contains("kemono") ? "kemono.cr" : videoUrl.Contains("coomer") ? "coomer.st" : "rule34.xxx";
                        string cookie = videoUrl.Contains("kemono") ? _kemonoSession : videoUrl.Contains("coomer") ? _coomerSession : "";
                        string headers = videoUrl.Contains("rule34")
                         ? "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://rule34.xxx/\r\n"
                         : $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://{domain}/\r\nCookie: session={cookie}";

                        double duration = await GetVideoDuration(videoUrl, headers);
                        post.VideoDuration = duration;
                        _durationCache.TryAdd(videoUrl, duration);
                    }

                    Interlocked.Increment(ref processed);
                    if (processed % 5 == 0 && progressCallback != null)
                    {
                        _ = progressCallback($"Processed video {processed}/{total}...");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to get duration for post {post.Id}: {ex.Message}");
                    post.VideoDuration = 0;
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task<List<ExternalPost>> FetchFromApi(string site, string user, int offset)
        {
            try
            {
                if (site.Contains("kemono"))
                {
                    var kemonoApi = new KemonoAPI(_kemonoLogger, _kemonoSession);
                    var results = await kemonoApi.FetchPostsAsync(ServiceType, user, offset);
                    return ConvertToExternalPosts(results);
                }
                else if (site.Contains("coomer"))
                {
                    var coomerApi = new CoomerAPI(_coomerLogger, _coomerSession);
                    var results = await coomerApi.FetchPostsAsync(ServiceType, user, offset);
                    return ConvertToExternalPosts(results);
                }
                else if (site.Contains("rule34"))
                {
                    var rule34Api = new Rule34API(_rule34Logger, _rule34UserId, _rule34ApiKey);
                    var results = await rule34Api.FetchPostsAsync(user, offset);
                    return ConvertToExternalPosts(results);
                }
                else
                {
                    _logger.LogWarning($"Unknown site: {site}");
                    return new List<ExternalPost>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching from API: {ex.Message}");
                return new List<ExternalPost>();
            }
        }

        private async Task<List<ExternalCreator>> FetchCreatorsFromApi(string site)
        {
            try
            {
                if (site.Contains("kemono"))
                {
                    var kemonoApi = new KemonoAPI(_kemonoLogger, _kemonoSession);
                    var results = await kemonoApi.FetchCreatorsAsync();
                    return ConvertToExternalCreators(results, "kemono.cr");
                }
                else if (site.Contains("coomer"))
                {
                    var coomerApi = new CoomerAPI(_coomerLogger, _coomerSession);
                    var results = await coomerApi.FetchCreatorsAsync();
                    return ConvertToExternalCreators(results, "coomer.st");
                }
                else if (site.Contains("rule34"))
                {
                    // Rule34 doesn't have a specific global creator fetch logic
                    return new List<ExternalCreator>();
                }
                else
                {
                    _logger.LogWarning($"Unknown site: {site}");
                    return new List<ExternalCreator>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching creators from API: {ex.Message}");
                return new List<ExternalCreator>();
            }
        }

        private List<ExternalPost> ConvertToExternalPosts(List<KemonoAPI.PostResult> results)
        {
            return results.Select(r => new ExternalPost
            {
                Id = r.Id,
                Title = r.Title,
                User = r.User,
                Service = r.Service,
                ThumbnailUrl = r.ThumbnailUrl,
                OriginalUrl = r.OriginalUrl,
                AttachmentCount = r.AttachmentCount,
                HasVideo = r.HasVideo,
                Published = r.Published,
                VideoDuration = 0
            }).ToList();
        }

        private List<ExternalPost> ConvertToExternalPosts(List<CoomerAPI.PostResult> results)
        {
            return results.Select(r => new ExternalPost
            {
                Id = r.Id,
                Title = r.Title,
                User = r.User,
                Service = r.Service,
                ThumbnailUrl = r.ThumbnailUrl,
                OriginalUrl = r.OriginalUrl,
                AttachmentCount = r.AttachmentCount,
                HasVideo = r.HasVideo,
                Published = r.Published,
                VideoDuration = 0
            }).ToList();
        }

        private List<ExternalPost> ConvertToExternalPosts(List<Rule34API.PostResult> results)
        {
            return results.Select(r => new ExternalPost
            {
                Id = r.Id,
                Title = r.Title,
                User = r.User,
                Service = r.Service,
                ThumbnailUrl = r.ThumbnailUrl,
                OriginalUrl = r.OriginalUrl,
                AttachmentCount = r.AttachmentCount,
                HasVideo = r.HasVideo,
                Published = r.Published,
                VideoDuration = 0
            }).ToList();
        }

        private List<ExternalCreator> ConvertToExternalCreators(List<KemonoAPI.CreatorResult> results, string domain)
        {
            return results.Select(r => new ExternalCreator
            {
                Id = r.Id,
                Name = r.Name,
                Service = r.Service,
                ProfileIconUrl = r.ProfileIconUrl,
                Updated = r.Updated,
                Indexed = r.Indexed,
                Favorited = r.Favorited,
                OriginalUrl = $"https://{domain}/{r.Service}/user/{r.Id}"
            }).ToList();
        }

        private List<ExternalCreator> ConvertToExternalCreators(List<CoomerAPI.CreatorResult> results, string domain)
        {
            return results.Select(r => new ExternalCreator
            {
                Id = r.Id,
                Name = r.Name,
                Service = r.Service,
                ProfileIconUrl = r.ProfileIconUrl,
                Updated = r.Updated,
                Indexed = r.Indexed,
                Favorited = r.Favorited,
                OriginalUrl = $"https://{domain}/{r.Service}/user/{r.Id}"
            }).ToList();
        }

        public async Task<IActionResult> OnGetVideoDurationAsync(string url)
        {
            if (string.IsNullOrEmpty(url) || !IsVideo(url))
            {
                return new JsonResult(new { duration = 0 });
            }

            if (_durationCache.TryGetValue(url, out double cachedDuration))
            {
                return new JsonResult(new { duration = cachedDuration });
            }

            try
            {
                string domain = url.Contains("kemono") ? "kemono.cr" : url.Contains("coomer") ? "coomer.st" : "rule34.xxx";
                string cookie = url.Contains("kemono") ? _kemonoSession : url.Contains("coomer") ? _coomerSession : "";
                string headers = url.Contains("rule34")
                 ? "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://rule34.xxx/\r\n"
                 : $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://{domain}/\r\nCookie: session={cookie}";

                double duration = await GetVideoDuration(url, headers);

                _durationCache.TryAdd(url, duration);

                return new JsonResult(new { duration });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting video duration: {ex.Message}");
                return new JsonResult(new { duration = 0 });
            }
        }

        public async Task<IActionResult> OnGetProxyImageAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return NotFound();

            if (IsVideo(url))
            {
                return await GenerateAndServeVideoFrame(url);
            }

            try
            {
                var handler = new HttpClientHandler
                {
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.All
                };

                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36");

                var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    return NotFound();
                }

                var memoryStream = new MemoryStream();
                await response.Content.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                return File(memoryStream, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Proxy Error: {ex.Message}");
                return NotFound();
            }
        }

        private async Task<IActionResult> GenerateAndServeVideoFrame(string videoUrl)
        {
            string thumbDir = Path.Combine(_env.WebRootPath, "VideoThumbnailsCache");
            if (!Directory.Exists(thumbDir)) Directory.CreateDirectory(thumbDir);

            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(videoUrl);
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));
            string filePath = Path.Combine(thumbDir, $"{hash}.jpg");

            if (System.IO.File.Exists(filePath))
            {
                return File(System.IO.File.OpenRead(filePath), "image/jpeg");
            }

            await _thumbGenerationLock.WaitAsync();
            try
            {
                if (System.IO.File.Exists(filePath)) return File(System.IO.File.OpenRead(filePath), "image/jpeg");

                string domain = videoUrl.Contains("kemono") ? "kemono.cr" : videoUrl.Contains("coomer") ? "coomer.st" : "rule34.xxx";
                string cookie = videoUrl.Contains("kemono") ? _kemonoSession : videoUrl.Contains("coomer") ? _coomerSession : "";
                string headers = videoUrl.Contains("rule34")
                 ? "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://rule34.xxx/\r\n"
                 : $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://{domain}/\r\nCookie: session={cookie}";

                double duration = await GetVideoDuration(videoUrl, headers);

                double seekTime = (duration >= 5.0) ? 5.0 : Math.Max(0, duration - 0.5);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -headers \"{headers}\" -ss {seekTime} -i \"{videoUrl}\" -vframes 1 -q:v 4 \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

                if (System.IO.File.Exists(filePath))
                {
                    return File(System.IO.File.OpenRead(filePath), "image/jpeg");
                }
                else
                {
                    return NotFound();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"FFmpeg Error: {ex.Message}");
                return NotFound();
            }
            finally
            {
                _thumbGenerationLock.Release();
            }
        }

        private async Task<double> GetVideoDuration(string url, string headers)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 -headers \"{headers}\" \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                StringBuilder output = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) output.Append(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

                if (double.TryParse(output.ToString(), out double duration))
                {
                    return duration;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private void LoadConfig()
        {
            try
            {
                if (System.IO.File.Exists(_configPath))
                {
                    var lines = System.IO.File.ReadAllLines(_configPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("COOMER_SESSION=")) _coomerSession = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("KEMONO_SESSION=")) _kemonoSession = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("PHPSESSID=")) _phpssessid = trimmed.Substring(10).Trim();
                        if (trimmed.StartsWith("EPRNS=")) _eprns = trimmed.Substring(6).Trim();
                        if (trimmed.StartsWith("CF_CLEARANCE=")) _cfClearence = trimmed.Substring(13).Trim();
                        if (trimmed.StartsWith("MANGAVIEW_COOKIE=")) _mangaViewCookie = trimmed.Substring(17).Trim();
                        if (trimmed.StartsWith("HENTAI_DUDE_AGENT=")) _hentaiDudeAgent = trimmed.Substring(18).Trim();
                        if (trimmed.StartsWith("RULE34_USER_ID=")) _rule34UserId = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("RULE34_API_KEY=")) _rule34ApiKey = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("REDDIT_COOKIE=")) _redditCookie = trimmed.Substring(14).Trim();
                        if (trimmed.StartsWith("THREADS="))
                        {
                            if (int.TryParse(trimmed.Substring(8).Trim(), out int t)) DOWNLOADERThreads = t;
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var lines = new List<string>
                {
                    $"PHPSESSID={_phpssessid}",
                    $"EPRNS={_eprns}",
                    $"COOMER_SESSION={_coomerSession}",
                    $"KEMONO_SESSION={_kemonoLogger}",
                    $"THREADS={DOWNLOADERThreads}",
                    $"CF_CLEARANCE={_cfClearence}",
                    $"MANGAVIEW_COOKIE={_mangaViewCookie}",
                    $"HENTAI_DUDE_AGENT={_hentaiDudeAgent}",
                    $"RULE34_USER_ID={_rule34UserId}",
                    $"RULE34_API_KEY={_rule34ApiKey}",
                    $"REDDIT_COOKIE={_redditCookie}",
                    "PATH=/app/wwwroot/Pending"
                };

                var dir = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                System.IO.File.WriteAllLines(_configPath, lines);
            }
            catch { /* Handle error */ }
        }

        public IActionResult OnPostUpdateSettings()
        {
            SaveConfig();
            return RedirectToPage();
        }

        private bool IsVideo(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path.Split('?')[0]).ToLower();
            return new[] { ".mp4", ".mkv", ".webm", ".mov", ".m4v" }.Contains(ext);
        }

        public IActionResult OnPostDownload([FromQuery] string url)
        {
            try
            {
                _logger.LogInformation($"Queueing download for URL: {url}");

                if (!string.IsNullOrWhiteSpace(url))
                {
                    _downloadManager.QueueUrl(url);
                    return new JsonResult(new { success = true, message = $"Queued: {url}" });
                }
                else
                {
                    _logger.LogWarning("Received empty URL for download.");
                    return new JsonResult(new { success = false, message = "URL was empty" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Download Error: {ex.Message}");
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }
    }
}