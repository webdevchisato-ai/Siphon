using System.Collections.Concurrent;
using System.Text.Json.Serialization; // <--- REQUIRED FOR [JsonIgnore]

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

        // --- FIX: Add [JsonIgnore] here ---
        // This prevents the "IntPtr" crash because this property 
        // will not be sent to the browser.
        [JsonIgnore]
        public CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();

        public bool IsCancelled { get; set; }
    }

    public class DownloadManager
    {
        private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly PreviewGenerator _previewGenerator;
        private readonly SemaphoreSlim _semaphore = new(3);

        public DownloadManager(IServiceProvider serviceProvider, PreviewGenerator previewGenerator)
        {
            _serviceProvider = serviceProvider;
            _previewGenerator = previewGenerator;
        }

        public IEnumerable<DownloadJob> GetJobs()
        {
            // CLEANUP: Remove finished jobs after 5 seconds
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
                    try { job.Cts.Cancel(); } catch { }
                }
            }
        }

        private async Task ProcessJob(DownloadJob job)
        {
            job.Status = "Waiting for slot...";
            try
            {
                // Wait for a thread slot, respecting the cancel token
                await _semaphore.WaitAsync(job.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                job.Status = "Cancelled";
                job.CompletedAt = DateTime.UtcNow;
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var downloader = scope.ServiceProvider.GetRequiredService<VideoDownloader>();

                await downloader.ProcessDownload(job);

                // If not cancelled, trigger preview generation
                if (!job.IsCancelled)
                {
                    job.CompletedAt = DateTime.UtcNow;
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
            }
            catch (Exception ex)
            {
                job.Status = $"Failed: {ex.Message}";
                job.IsError = true;
                job.CompletedAt = DateTime.UtcNow;
            }
            finally
            {
                _semaphore.Release();
                try { job.Cts.Dispose(); } catch { }
            }
        }
    }
}