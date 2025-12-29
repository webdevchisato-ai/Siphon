namespace Siphon.Services
{
    public class RetentionService : IHostedService, IDisposable
    {
        private readonly ILogger<RetentionService> _logger;
        private readonly UserService _userService;
        private readonly IWebHostEnvironment _env;
        private Timer _timer;

        public RetentionService(ILogger<RetentionService> logger, UserService userService, IWebHostEnvironment env)
        {
            _logger = logger;
            _userService = userService;
            _env = env;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // 1. Get the configured time on startup
            int intervalMinutes = _userService.GetPreservationMinutes();
            if (intervalMinutes <= 0) intervalMinutes = 60; // Safety default

            _logger.LogInformation($"Retention Service started. Schedule: Every {intervalMinutes} minutes.");

            // Start timer: Run immediately once (TimeSpan.Zero), then repeat every 'intervalMinutes'
            _timer = new Timer(CleanupFiles, null, TimeSpan.Zero, TimeSpan.FromMinutes(intervalMinutes));

            return Task.CompletedTask;
        }

        // --- NEW: Update the timer without forcing immediate execution ---
        public void UpdateTimerInterval()
        {
            int newMinutes = _userService.GetPreservationMinutes();
            if (newMinutes <= 0) newMinutes = 60;

            _logger.LogInformation($"Retention Interval updated. Next check in {newMinutes} minutes.");

            // Change(dueTime, period)
            // dueTime = newMinutes (Wait this long before the next run)
            // period = newMinutes (Repeat interval)
            _timer?.Change(TimeSpan.FromMinutes(newMinutes), TimeSpan.FromMinutes(newMinutes));
        }

        private void CleanupFiles(object state)
        {
            try
            {
                int maxMinutes = _userService.GetPreservationMinutes();
                if (maxMinutes <= 0) return;

                var pendingDir = Path.Combine(_env.WebRootPath, "Pending");
                if (!Directory.Exists(pendingDir)) return;

                var cutoffTime = DateTime.UtcNow.AddMinutes(-maxMinutes);

                // Get only main video files
                var files = new DirectoryInfo(pendingDir).GetFiles("*.mp4")
                    .Where(f => !f.Name.EndsWith("_preview.mp4"));

                foreach (var file in files)
                {
                    if (file.CreationTimeUtc < cutoffTime)
                    {
                        _logger.LogInformation($"Retention Policy: Deleting old file {file.Name} (Age: {DateTime.UtcNow - file.CreationTimeUtc})");

                        try
                        {
                            // Delete Main Video
                            file.Delete();

                            // Delete Thumbnail
                            string thumb = Path.ChangeExtension(file.FullName, ".jpg");
                            if (File.Exists(thumb)) File.Delete(thumb);

                            // Delete Preview
                            string preview = file.FullName.Replace(".mp4", "_preview.mp4");
                            if (File.Exists(preview)) File.Delete(preview);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Failed to delete {file.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Retention Service Error: {ex.Message}");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose() => _timer?.Dispose();
    }
}