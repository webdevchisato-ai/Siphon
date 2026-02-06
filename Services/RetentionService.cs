using System.Text.Json;

namespace Siphon.Services
{
    public class RetentionService : IHostedService, IDisposable
    {
        private readonly ILogger<RetentionService> _logger;
        private readonly UserService _userService;
        private readonly IWebHostEnvironment _env;
        private Timer pendingTimmer;
        private Timer approvedTimmer;
        private Timer fileURLTimer;

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
            pendingTimmer = new Timer(CleanupPendingFiles, null, TimeSpan.Zero, TimeSpan.FromMinutes(intervalMinutes));

            int approvedRetention = _userService.GetApprovedRetentionMinutes();
            if (intervalMinutes <= 0) intervalMinutes = 60;

            _logger.LogInformation($"Approved Retention Service started. Schedule: Every {approvedRetention} minutes.");

            approvedTimmer = new Timer(CleanupApprovedFiles, null, TimeSpan.Zero, TimeSpan.FromMinutes(approvedRetention));

            int fileURLMinutes = _userService.GetPreservationMinutes();
            if (fileURLMinutes <= 0) fileURLMinutes = 60; // Safety default

            _logger.LogInformation($"File URL Service started. Schedule: Every {fileURLMinutes} minutes.");

            // Start timer: Run immediately once (TimeSpan.Zero), then repeat every 'intervalMinutes'
            pendingTimmer = new Timer(CleanupFileURLStorage, null, TimeSpan.Zero, TimeSpan.FromMinutes(fileURLMinutes));

            return Task.CompletedTask;
        }

        // --- NEW: Update the timer without forcing immediate execution ---
        public void UpdateTimerInterval()
        {
            int pendingMins = _userService.GetPreservationMinutes();
            int approvedMins = _userService.GetApprovedRetentionMinutes();
            if (pendingMins <= 0) pendingMins = 60;
            if (approvedMins <= 0) approvedMins = 60;

            _logger.LogInformation($"Pending Retention Interval updated. Next check in {pendingMins} minutes.");
            _logger.LogInformation($"Approved Retention Interval updated. Next check in {approvedMins} minutes.");

            // Change(dueTime, period)
            // dueTime = newMinutes (Wait this long before the next run)
            // period = newMinutes (Repeat interval)
            pendingTimmer?.Change(TimeSpan.FromMinutes(pendingMins), TimeSpan.FromMinutes(pendingMins));
            approvedTimmer?.Change(TimeSpan.FromMinutes(approvedMins), TimeSpan.FromMinutes(approvedMins));
            fileURLTimer?.Change(TimeSpan.FromMinutes(pendingMins), TimeSpan.FromMinutes(pendingMins)); //timer is the same as pending
        }

        private void CleanupApprovedFiles(object state)
        {
            _logger.LogInformation("Approved: Running retention cleanup.");
            try
            {
                int maxMinutes = _userService.GetApprovedRetentionMinutes();
                if (maxMinutes <= 0) return;

                var cutoffTime = DateTime.UtcNow.AddMinutes(-maxMinutes);

                var configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "extra_dirs.json");
                List<string> ApprovalDirectories = new List<string>();
                if (System.IO.File.Exists(configPath))
                {
                    try
                    {
                        var json = System.IO.File.ReadAllText(configPath);
                        var extras = JsonSerializer.Deserialize<List<string>>(json);
                        if (extras != null)
                        {
                            // Prefix them so we know they go into "Extra_Approved"
                            ApprovalDirectories.AddRange(extras.Select(d => $"Extra_Approved/{d}"));
                        }
                        ApprovalDirectories.Add("Approved");
                    }
                    catch { }
                }

                foreach (var dir in ApprovalDirectories)
                {
                    var approvedDir = Path.Combine(_env.WebRootPath, dir);
                    if (!Directory.Exists(approvedDir)) continue;
                    var files = new DirectoryInfo(approvedDir).GetFiles();
                    foreach (var file in files)
                    {
                        if (file.CreationTimeUtc < cutoffTime)
                        {
                            _logger.LogInformation($"Retention Policy: Deleting old approved file {file.Name} from {dir} (Age: {DateTime.UtcNow - file.CreationTimeUtc})");
                            try
                            {
                                file.Delete();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"Failed to delete approved file {file.Name} from {dir}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Approved: Retention Service Error: {ex.Message}");
            }
        }

        private void CleanupPendingFiles(object state)
        {
            _logger.LogInformation("Pending: Running retention cleanup.");
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
                        _logger.LogInformation($"Retention Policy: Deleting old pending file {file.Name} (Age: {DateTime.UtcNow - file.CreationTimeUtc})");

                        try
                        {
                            // Delete Main Video
                            file.Delete();

                            // Delete Thumbnail
                            string thumb = file.FullName.Replace(".mp4", "_preview.jpg");
                            if (File.Exists(thumb)) File.Delete(thumb);

                            // Delete Preview
                            string preview = file.FullName.Replace(".mp4", "_preview.mp4");
                            if (File.Exists(preview)) File.Delete(preview);

                            string heatmap = file.FullName.Replace(".mp4", ".json");
                            if (File.Exists(heatmap)) File.Delete(heatmap);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Failed pending to delete {file.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Pending: Retention Service Error: {ex.Message}");
            }
        }

        private void CleanupFileURLStorage(object state)
        {
            _logger.LogInformation("Pending: File URL retention cleanup.");
            try
            {
                int maxMinutes = _userService.GetPreservationMinutes();
                if (maxMinutes <= 0) return;

                string pendingFilePath = Path.Combine(_env.WebRootPath, "Lookups", "PendingFileURLs.json");

                if (File.Exists(pendingFilePath))
                {
                    _logger.LogInformation($"Deleting pending file URL storage");
                    File.Delete(pendingFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"File URL Retention Service Error: {ex.Message}");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            pendingTimmer?.Change(Timeout.Infinite, 0);
            approvedTimmer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            pendingTimmer?.Dispose();
            approvedTimmer?.Dispose();
        }
    }
}