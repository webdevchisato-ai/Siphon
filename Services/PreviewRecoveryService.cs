using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting; // Required for IWebHostEnvironment
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Siphon.Services
{
    public class PreviewRecoveryService : BackgroundService
    {
        private readonly PreviewGenerator _previewGenerator;
        private readonly ILogger<PreviewRecoveryService> _logger;
        private readonly string _pendingPath;

        // Inject IWebHostEnvironment to safely get the path to wwwroot
        public PreviewRecoveryService(
            PreviewGenerator previewGenerator,
            ILogger<PreviewRecoveryService> logger,
            IWebHostEnvironment env)
        {
            _previewGenerator = previewGenerator;
            _logger = logger;

            // This dynamically builds: "C:\YourApp\wwwroot\Pending"
            _pendingPath = Path.Combine(env.WebRootPath, "Pending");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"Preview Recovery Service watching: {_pendingPath}");

            // 1. Run immediately on startup
            await ScanAndRecover();

            // 2. Set up a 1-hour periodic timer
            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(30));

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ScanAndRecover();
            }
        }

        private async Task ScanAndRecover()
        {
            try
            {
                if (!Directory.Exists(_pendingPath))
                {
                    // Silent return or debug log is usually fine here if the folder 
                    // hasn't been created yet by the downloader.
                    return;
                }

                _logger.LogInformation($"Preview Recovery Service Auto Scanning: {_pendingPath}");

                // Get all MP4 files in wwwroot/Pending
                var videoFiles = Directory.GetFiles(_pendingPath, "*.mp4", SearchOption.TopDirectoryOnly); 

                foreach (var videoPath in videoFiles)
                {
                    // Skip the actual preview files themselves
                    if (videoPath.EndsWith("_preview.mp4", StringComparison.OrdinalIgnoreCase)) continue;

                    string thumbPath = videoPath.Replace(".mp4", "_preview.jpg");
                    string previewPath = videoPath.Replace(".mp4", "_preview.mp4");
                    string heatmapPath = videoPath.Replace(".mp4", ".json");

                    // Check if either the thumbnail or the preview video is missing
                    bool missingThumbnail = !File.Exists(thumbPath);
                    bool missingPreview = !File.Exists(previewPath);
                    bool missingHeatmap = !File.Exists(heatmapPath);

                    if (missingThumbnail || missingPreview || missingHeatmap)
                    {
                        // Ensure we don't queue something currently being processed
                        if (!_previewGenerator.IsProcessing(videoPath))
                        {
                            _logger.LogInformation($"[Recovery] Found missing assets for: {Path.GetFileName(videoPath)}. Queuing generation.");
                            _previewGenerator.QueueGeneration(videoPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during Preview Recovery scan.");
            }
        }
    }
}