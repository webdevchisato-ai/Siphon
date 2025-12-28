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
            // Start timer: Run Cleanup immediately, then every 1 hour
            _timer = new Timer(CleanupFiles, null, TimeSpan.Zero, TimeSpan.FromHours(1));
            return Task.CompletedTask;
        }

        private void CleanupFiles(object state)
        {
            try
            {
                int maxMinutes = _userService.GetPreservationMinutes();
                // If set to 0 or negative (disabled), do nothing
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