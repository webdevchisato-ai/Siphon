using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
            IWebHostEnvironment environment)
        {
            _logger = logger;
            _kemonoLogger = kemonoLogger;
            _coomerLogger = coomerLogger;
            _downloadManager = downloadManager;
            _env = environment;
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

        public async Task OnGetAsync()
        {
            string cacheFolder = Path.Combine(_env.WebRootPath, "ExternalIntegrationCache");
            if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

            string scope = string.IsNullOrWhiteSpace(SearchUser) ? "Global" : $"User_{SearchUser}";
            if (ServiceType != "all") scope += $"_{ServiceType}";

            string cacheFileName = $"Feed_{Site}_{scope}_Offset{Offset}.json";
            string cachePath = Path.Combine(cacheFolder, cacheFileName);

            List<ExternalPost> rawPosts = null;

            // Try to load from cache
            if (System.IO.File.Exists(cachePath) && System.IO.File.GetLastWriteTime(cachePath) > DateTime.Now.AddDays(-1))
            {
                try
                {
                    string json = await System.IO.File.ReadAllTextAsync(cachePath);
                    rawPosts = JsonSerializer.Deserialize<List<ExternalPost>>(json);
                    if (rawPosts != null) SearchTriggered = true;
                }
                catch { }
            }

            // Fetch from API if not cached
            if (rawPosts == null)
            {
                if (!SearchTriggered) return;
                rawPosts = await FetchFromApi(Site, SearchUser, Offset);

                if (rawPosts != null && rawPosts.Count > 0)
                {
                    // Enrich with video durations before caching
                    await EnrichWithVideoDurations(rawPosts);

                    string json = JsonSerializer.Serialize(rawPosts, new JsonSerializerOptions { WriteIndented = true });
                    await System.IO.File.WriteAllTextAsync(cachePath, json);
                }
            }
            else
            {
                // Even if cached, check if we need to enrich posts that don't have duration yet
                var postsNeedingDuration = rawPosts.Where(p => p.HasVideo && p.VideoDuration == 0).ToList();
                if (postsNeedingDuration.Any())
                {
                    await EnrichWithVideoDurations(postsNeedingDuration);
                    // Update cache
                    string json = JsonSerializer.Serialize(rawPosts, new JsonSerializerOptions { WriteIndented = true });
                    await System.IO.File.WriteAllTextAsync(cachePath, json);
                }
            }

            if (rawPosts == null) return;

            // Apply filters
            var filtered = rawPosts.AsEnumerable();
            if (OnlyVideos) filtered = filtered.Where(p => p.HasVideo);
            if (MinAttachments > 0) filtered = filtered.Where(p => p.AttachmentCount >= MinAttachments);

            // Apply video length filter if enabled
            if (EnableVideoLengthFilter)
            {
                filtered = filtered.Where(p =>
                    !p.HasVideo || // Include non-videos
                    (p.VideoDuration >= MinVideoLength && p.VideoDuration <= MaxVideoLength)
                );
            }

            Posts = filtered.ToList();
        }

        // --- ENRICH POSTS WITH VIDEO DURATIONS ---
        private async Task EnrichWithVideoDurations(List<ExternalPost> posts)
        {
            var videoPosts = posts.Where(p => p.HasVideo && !string.IsNullOrEmpty(p.ThumbnailUrl)).ToList();

            if (!videoPosts.Any()) return;

            _logger.LogInformation($"Enriching {videoPosts.Count} video posts with duration data...");

            var tasks = videoPosts.Select(async post =>
            {
                try
                {
                    string videoUrl = post.ThumbnailUrl;

                    // Check if already cached in memory
                    if (_durationCache.TryGetValue(videoUrl, out double cachedDuration))
                    {
                        post.VideoDuration = cachedDuration;
                        return;
                    }

                    string domain = videoUrl.Contains("kemono") ? "kemono.cr" : "coomer.st";
                    string cookie = videoUrl.Contains("kemono") ? _kemonoSession : _coomerSession;
                    string headers = $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36\r\nReferer: https://{domain}/\r\nCookie: session={cookie}";

                    double duration = await GetVideoDuration(videoUrl, headers);
                    post.VideoDuration = duration;

                    // Cache it
                    _durationCache.TryAdd(videoUrl, duration);
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