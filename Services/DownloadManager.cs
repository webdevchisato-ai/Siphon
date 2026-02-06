using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Siphon.Services
{
    public class DownloadJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Url { get; set; }
        public string Status { get; set; } = "Queued";
        public double Progress { get; set; }
        public string DownloadSpeed { get; set; } = "0 KB/s";

        public string ThumbnailUrl { get; set; }
        public string Filename { get; set; }
        public bool IsError { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string FinalFilePath { get; set; }

        [JsonIgnore]
        public CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();

        public bool IsCancelled { get; set; }
    }

    public class DownloadManager
    {
        private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly PreviewGenerator _previewGenerator;
        private readonly ILogger<DownloadManager> _logger;
        private readonly IWebHostEnvironment _env;

        private volatile SemaphoreSlim _semaphore;
        private readonly string _configPath;
        private int _currentThreadLimit = 3;

        public DownloadManager(IServiceProvider serviceProvider, PreviewGenerator previewGenerator, ILogger<DownloadManager> logger, IWebHostEnvironment env)
        {
            _serviceProvider = serviceProvider;
            _previewGenerator = previewGenerator;
            _logger = logger;
            _env = env;
            _configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "scraper_config.txt");

            LoadAndApplyConfig();
        }

        public void ReloadConfig()
        {
            _logger.LogInformation("Reloading configuration manually requested.");
            LoadAndApplyConfig();
        }

        private void LoadAndApplyConfig()
        {
            try
            {
                int newThreads = 3;
                if (File.Exists(_configPath))
                {
                    var lines = File.ReadAllLines(_configPath);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("THREADS="))
                        {
                            if (int.TryParse(line.Substring(8).Trim(), out int t) && t > 0)
                            {
                                newThreads = t;
                            }
                        }
                    }
                }

                if (_semaphore == null || newThreads != _currentThreadLimit)
                {
                    _currentThreadLimit = newThreads;
                    _semaphore = new SemaphoreSlim(_currentThreadLimit);
                    _logger.LogInformation($"Configuration loaded. Concurrency limit set to: {_currentThreadLimit}");
                }
                else
                {
                    _logger.LogInformation("Configuration reloaded. Thread count unchanged.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading configuration");
                if (_semaphore == null) _semaphore = new SemaphoreSlim(3);
            }
        }

        public IEnumerable<DownloadJob> GetJobs()
        {
            var expiredJobs = _jobs.Values
                .Where(j => j.CompletedAt.HasValue && (DateTime.UtcNow - j.CompletedAt.Value).TotalSeconds > 5)
                .Select(j => j.Id)
                .ToList();

            foreach (var id in expiredJobs) _jobs.TryRemove(id, out _);

            return _jobs.Values;
        }

        public void QueueUrl(string url)
        {
            var job = new DownloadJob { Url = url };
            _jobs.TryAdd(job.Id, job);
            _logger.LogInformation($"Job queued: {url} [ID: {job.Id}]");
            Task.Run(() => ProcessJob(job));
        }

        public void CancelJob(string id)
        {
            if (_jobs.TryGetValue(id, out var job))
            {
                if (!job.IsCancelled && job.CompletedAt == null)
                {
                    job.IsCancelled = true;
                    job.Status = "Cancelling...";
                    _logger.LogInformation($"Job cancellation requested: {job.Id}");
                    try { job.Cts.Cancel(); } catch { }

                    // Immediate Cleanup on Cancel Request
                    CleanupJobFiles(job);
                    CleanupPreview(job.Id);
                }
            }
        }

        private async Task ProcessJob(DownloadJob job)
        {
            job.Status = "Waiting for slot...";

            SemaphoreSlim currentSemaphore = _semaphore;

            try
            {
                await currentSemaphore.WaitAsync(job.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                job.Status = "Cancelled";
                job.CompletedAt = DateTime.UtcNow;
                CleanupJobFiles(job);
                CleanupPreview(job.Id);
                return;
            }

            try
            {
                _logger.LogInformation($"Starting download for job: {job.Id}");
                using var scope = _serviceProvider.CreateScope();
                var downloader = scope.ServiceProvider.GetRequiredService<VideoDownloader>();

                await downloader.ProcessDownload(job);

                if (!job.IsCancelled)
                {
                    job.CompletedAt = DateTime.UtcNow;
                    _logger.LogInformation($"Job completed successfully: {job.Id}");
                    if (!string.IsNullOrEmpty(job.FinalFilePath) && File.Exists(job.FinalFilePath))
                    {
                        _previewGenerator.QueueGeneration(job.FinalFilePath);
                    }

                    AddURLToPendingFiles(job.Filename, job.Url);
                }
            }
            catch (OperationCanceledException)
            {
                job.Status = "Cancelled";
                job.IsError = true;
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogWarning($"Job cancelled during process: {job.Id}");

                // Cleanup on Cancel
                CleanupJobFiles(job);
            }
            catch (Exception ex)
            {
                job.Status = $"Failed: {ex.Message}";
                job.IsError = true;
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogError(ex, ($"Job failed: {job.Id}"));

                // Cleanup on Error
                CleanupJobFiles(job);
            }
            finally
            {
                currentSemaphore.Release();
                try { job.Cts.Dispose(); } catch { }
                CleanupPreview(job.Id);
            }
        }

        //currently unimplimented, will be added in next release (will show url in the pending files and aprroved pages)
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

        private void CleanupJobFiles(DownloadJob job)
        {
            try
            {
                // 1. Delete the "Final" file if it was partially created
                if (!string.IsNullOrEmpty(job.FinalFilePath) && File.Exists(job.FinalFilePath))
                {
                    File.Delete(job.FinalFilePath);
                    _logger.LogDebug($"Cleaned up file: {job.FinalFilePath}");
                }

                // 2. Clean up partial downloads (e.g. .mp4.part, .ytdl, etc.)
                if (!string.IsNullOrEmpty(job.Filename))
                {
                    string pendingDir = Path.Combine(_env.WebRootPath, "Pending");
                    if (Directory.Exists(pendingDir))
                    {
                        // Match filename.* to catch .part, .temp, .ytdl
                        var partials = Directory.GetFiles(pendingDir, $"{job.Filename}.*");
                        foreach (var p in partials)
                        {
                            try { File.Delete(p); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error cleaning up job files for {job.Id}: {ex.Message}");
            }
        }

        private void CleanupPreview(string jobId)
        {
            try
            {
                string previewDir = Path.Combine(_env.WebRootPath, "PreviewImages");
                if (Directory.Exists(previewDir))
                {
                    var files = Directory.GetFiles(previewDir, $"{jobId}.*");
                    foreach (var file in files)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    public class PendingVideoUrlContainer
    {
        public Dictionary<string, string> Urls { get; set; } = new Dictionary<string, string>(); //<filename, url>
    }
}