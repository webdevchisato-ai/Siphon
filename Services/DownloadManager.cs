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

        // Use volatile to ensure thread safety when swapping semaphores during hot reload
        private volatile SemaphoreSlim _semaphore;
        private readonly string _configPath;
        private int _currentThreadLimit = 3;

        public DownloadManager(IServiceProvider serviceProvider, PreviewGenerator previewGenerator, ILogger<DownloadManager> logger)
        {
            _serviceProvider = serviceProvider;
            _previewGenerator = previewGenerator;
            _logger = logger;
            _configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "scraper_config.txt");

            // Initial Config Load
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
                int newThreads = 3; // Default
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
                    // Replace the semaphore. Old jobs will release the instance they grabbed.
                    // New jobs will grab this new one.
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
                }
            }
        }

        private async Task ProcessJob(DownloadJob job)
        {
            job.Status = "Waiting for slot...";

            // Capture the specific semaphore instance active at this moment
            SemaphoreSlim currentSemaphore = _semaphore;

            try
            {
                await currentSemaphore.WaitAsync(job.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                job.Status = "Cancelled";
                job.CompletedAt = DateTime.UtcNow;
                return;
            }

            try
            {
                _logger.LogInformation($"Starting download for job: {job.Id}");
                using var scope = _serviceProvider.CreateScope();
                // VideoDownloader will reload config internally on instantiation to get fresh Cookies
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
                }
            }
            catch (OperationCanceledException)
            {
                job.Status = "Cancelled";
                job.IsError = true;
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogWarning($"Job cancelled during process: {job.Id}");
            }
            catch (Exception ex)
            {
                job.Status = $"Failed: {ex.Message}";
                job.IsError = true;
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogError(ex, ($"Job failed: {job.Id}"));
            }
            finally
            {
                // Release the specific semaphore instance we waited on
                currentSemaphore.Release();
                try { job.Cts.Dispose(); } catch { }
            }
        }
    }
}