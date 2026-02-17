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

namespace Siphon.Pages
{
    public class ExternalIntegrationsModel : PageModel
    {
        private readonly ILogger<ExternalIntegrationsModel> _logger;
        private readonly ILogger<KemonoAPI> _kemonoLogger;
        private readonly ILogger<CoomerAPI> _coomerLogger;
        private readonly DownloadManager _downloadManager;
        private readonly IWebHostEnvironment _env;
        private readonly ICompositeViewEngine _viewEngine;
        private readonly ITempDataProvider _tempDataProvider;
        private readonly string _configPath;
        private string _kemonoSession = "";
        private string _coomerSession = "";

        // Limit concurrent FFmpeg operations
        private static readonly SemaphoreSlim _thumbGenerationLock = new SemaphoreSlim(3);

        // Cache for video durations to avoid repeated ffprobe calls
        private static readonly ConcurrentDictionary<string, double> _durationCache = new ConcurrentDictionary<string, double>();

        public ExternalIntegrationsModel(
            ILogger<ExternalIntegrationsModel> logger,
            ILogger<KemonoAPI> kemonoLogger,
            ILogger<CoomerAPI> coomerLogger,
            DownloadManager downloadManager,
            IWebHostEnvironment environment,
            ICompositeViewEngine viewEngine,
            ITempDataProvider tempDataProvider)
        {
            _logger = logger;
            _kemonoLogger = kemonoLogger;
            _coomerLogger = coomerLogger;
            _downloadManager = downloadManager;
            _env = environment;
            _viewEngine = viewEngine;
            _tempDataProvider = tempDataProvider;
            _configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "scraper_config.txt");
            LoadConfig();
        }

        public List<ExternalPost> Posts { get; set; } = new();

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

            // --- SAFETY SETTINGS ---
            int retryCount = 0;
            int maxRetries = 20; // Prevent infinite loops. Stop after checking 20 pages (1000 posts).
            bool keepSearching = true;

            // We use a loop to handle the "OnlyVideos" retry logic
            while (keepSearching)
            {
                string cacheFolder = Path.Combine(_env.WebRootPath, "ExternalIntegrationCache");
                if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

                string scope = string.IsNullOrWhiteSpace(SearchUser) ? "Global" : $"User_{SearchUser}";
                if (ServiceType != "all") scope += $"_{ServiceType}";

                // Note: Offset is part of the filename, so this updates every loop iteration
                string cacheFileName = $"Feed_{Site}_{scope}_Offset{Offset}.json";
                string cachePath = Path.Combine(cacheFolder, cacheFileName);

                List<ExternalPost> rawPosts = null;

                // Try to load from cache
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

                // Fetch from API if not cached
                if (rawPosts == null)
                {
                    if (!SearchTriggered) return new EmptyResult();

                    await SendEvent("status", $"Contacting API (Offset {Offset})...");

                    // --- TIMEOUT LOGIC START ---
                    var totalTimeout = TimeSpan.FromMinutes(3);
                    var stopwatch = Stopwatch.StartNew();

                    // 1. Fetch Data Task
                    var fetchTask = FetchFromApi(Site, SearchUser, Offset);

                    // Wait for Fetch OR Timeout
                    var completedFetch = await Task.WhenAny(fetchTask, Task.Delay(totalTimeout));

                    if (completedFetch != fetchTask)
                    {
                        // CASE 1: API Connection Timed Out
                        IsSearchTimeout = true;
                        timeOutType = TimeOutType.StandardTimeout;
                        _logger.LogWarning("Search timed out during API fetch phase.");

                        await SendEvent("error", "Search timed out contacting API.");
                        return new EmptyResult(); // EXIT HERE
                    }
                    else
                    {
                        // Fetch completed successfully
                        rawPosts = await fetchTask;

                        if (rawPosts.Count() > 0)
                        {
                            if (rawPosts[0].Id.Contains("Not Found"))
                            {
                                // CASE 2: User Not Found
                                IsSearchTimeout = true;
                                timeOutType = TimeOutType.NotFound;

                                // Send Warning and EXIT
                                await SendEvent("warning", "User not found. Please check the ID and Service.");
                                return new EmptyResult(); // EXIT HERE
                            }
                            else if (rawPosts[0].Id.Contains("End Of Posts"))
                            {
                                // CASE 3: End of Posts - WE MUST STOP LOOPING HERE
                                timeOutType = TimeOutType.EndOfPosts;
                                IsSearchTimeout = true;

                                await SendEvent("warning", "Reached the end of available posts.");

                                // If we were looking for videos and found none by the end, we still have to exit.
                                return new EmptyResult(); // EXIT HERE
                            }

                            if (rawPosts != null && rawPosts.Count > 0)
                            {
                                // Calculate remaining time for enrichment
                                var elapsed = stopwatch.Elapsed;
                                var remaining = totalTimeout - elapsed;

                                if (remaining > TimeSpan.Zero)
                                {
                                    // Notify user via stream
                                    await SendEvent("status", $"Found {rawPosts.Count} raw posts. Enriching metadata...");

                                    // 2. Enrichment Task
                                    var enrichTask = EnrichWithVideoDurations(rawPosts, (msg) => SendEvent("status", msg));

                                    // Wait for Enrichment OR Remaining Timeout
                                    var completedEnrich = await Task.WhenAny(enrichTask, Task.Delay(remaining));

                                    if (completedEnrich != enrichTask)
                                    {
                                        // CASE 4: Enrichment Timeout (We allow partial results, so we don't exit, just warn)
                                        IsSearchTimeout = true;
                                        _logger.LogWarning("Search timed out during Video Enrichment phase.");
                                        await SendEvent("warning", "Timeout during enrichment. Some metadata may be missing.");
                                    }
                                }
                                else
                                {
                                    IsSearchTimeout = true;
                                }

                                // Update cache only if we didn't timeout
                                if (!IsSearchTimeout)
                                {
                                    string json = JsonSerializer.Serialize(rawPosts, new JsonSerializerOptions { WriteIndented = true });
                                    await System.IO.File.WriteAllTextAsync(cachePath, json);
                                }
                            }
                        }
                        else
                        {
                            // CASE 5: Empty API Result (Unknown Error)
                            IsSearchTimeout = true;
                            timeOutType = TimeOutType.ApiIssue;

                            await SendEvent("error", "API returned unknown or empty data.");
                            return new EmptyResult(); // EXIT HERE
                        }
                    }
                    // --- TIMEOUT LOGIC END ---
                }
                else
                {
                    await SendEvent("status", $"Found {rawPosts.Count} Cached Posts. Processing...");

                    // Even if cached, check if we need to enrich posts that don't have duration yet
                    var postsNeedingDuration = rawPosts.Where(p => p.HasVideo && p.VideoDuration == 0).ToList();
                    if (postsNeedingDuration.Any())
                    {
                        await SendEvent("status", $"Enriching {postsNeedingDuration.Count} cached video items...");
                        var enrichTask = EnrichWithVideoDurations(postsNeedingDuration, (msg) => SendEvent("status", msg));
                        var completed = await Task.WhenAny(enrichTask, Task.Delay(TimeSpan.FromMinutes(3)));

                        if (completed != enrichTask) IsSearchTimeout = true;

                        // Update cache if fully successful
                        if (!IsSearchTimeout)
                        {
                            string json = JsonSerializer.Serialize(rawPosts, new JsonSerializerOptions { WriteIndented = true });
                            await System.IO.File.WriteAllTextAsync(cachePath, json);
                        }
                    }
                }

                if (rawPosts == null) rawPosts = new List<ExternalPost>();

                // Apply filters
                var filtered = rawPosts.AsEnumerable();

                if (OnlyVideos) filtered = filtered.Where(p => p.HasVideo);

                // --- RETRY LOGIC START ---
                bool shouldRetry = false;

                if (OnlyVideos && filtered.Count() == 0)
                {
                    // We wanted videos, but this page has none. 
                    // Increment offset and loop again.
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
                    // We found results (or we aren't filtering strict videos), so we break the loop and render
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
                // --- RETRY LOGIC END ---
            }

            // --- CRITICAL FIX: Tell frontend the correct next offset ---
            int correctNextOffset = Offset + 50;
            await SendEvent("metaOffset", JsonSerializer.Serialize(new { nextOffset = correctNextOffset }));

            await SendEvent("status", "Rendering results...");

            // Render Partial View to String
            string html = await RenderPartialToStringAsync("_PostGrid", Posts);

            // Send final HTML
            await SendEvent("result", html);

            // FIX: Prevent further rendering by the framework
            return new EmptyResult();
        }

        // Helper to render Partial View to String
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

        // --- ENRICH POSTS WITH VIDEO DURATIONS ---
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
                        string domain = videoUrl.Contains("kemono") ? "kemono.cr" : "coomer.st";
                        string cookie = videoUrl.Contains("kemono") ? _kemonoSession : _coomerSession;
                        string headers = $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://{domain}/\r\nCookie: session={cookie}";

                        double duration = await GetVideoDuration(videoUrl, headers);
                        post.VideoDuration = duration;
                        _durationCache.TryAdd(videoUrl, duration);
                    }

                    Interlocked.Increment(ref processed);
                    // Optional: Send granular updates every 5 items to avoid flooding the stream
                    if (processed % 5 == 0 && progressCallback != null)
                    {
                        // Fire and forget the status update to not block
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

        // --- DECIDE WHICH API SERVICE TO USE ---
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

        // Convert API results to ExternalPost format
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
                VideoDuration = 0 // Will be enriched later
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
                VideoDuration = 0 // Will be enriched later
            }).ToList();
        }

        // --- VIDEO DURATION HANDLER ---
        public async Task<IActionResult> OnGetVideoDurationAsync(string url)
        {
            if (string.IsNullOrEmpty(url) || !IsVideo(url))
            {
                return new JsonResult(new { duration = 0 });
            }

            // Check cache first
            if (_durationCache.TryGetValue(url, out double cachedDuration))
            {
                return new JsonResult(new { duration = cachedDuration });
            }

            try
            {
                string domain = url.Contains("kemono") ? "kemono.cr" : "coomer.st";
                string cookie = url.Contains("kemono") ? _kemonoSession : _coomerSession;
                string headers = $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://{domain}/\r\nCookie: session={cookie}";

                double duration = await GetVideoDuration(url, headers);

                // Cache the result
                _durationCache.TryAdd(url, duration);

                return new JsonResult(new { duration });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting video duration: {ex.Message}");
                return new JsonResult(new { duration = 0 });
            }
        }

        // --- PROXY HANDLER (Images & Video Frames) ---
        public async Task<IActionResult> OnGetProxyImageAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return NotFound();

            // 1. Intercept Video URLs -> Generate Frame
            if (IsVideo(url))
            {
                return await GenerateAndServeVideoFrame(url);
            }

            // 2. Standard Image Proxy
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

        // --- VIDEO PREVIEW LOGIC (5s or Last Frame) ---
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

                string domain = videoUrl.Contains("kemono") ? "kemono.cr" : "coomer.st";
                string cookie = videoUrl.Contains("kemono") ? _kemonoSession : _coomerSession;
                string headers = $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://{domain}/\r\nCookie: session={cookie}";

                // A. Get Duration
                double duration = await GetVideoDuration(videoUrl, headers);

                // B. Calculate Seek Time (5s or 0.5s from end if shorter)
                double seekTime = (duration >= 5.0) ? 5.0 : Math.Max(0, duration - 0.5);

                // C. Extract Frame
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
                    }
                }
            }
            catch { }
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