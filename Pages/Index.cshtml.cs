using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;

namespace Siphon.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly TorProxyManager _torManager;
        private readonly DownloadManager _downloadManager;

        public IndexModel(ILogger<IndexModel> logger, IWebHostEnvironment env, TorProxyManager torManager, DownloadManager downloadManager)
        {
            _logger = logger;
            _env = env;
            _torManager = torManager;
            _downloadManager = downloadManager;
        }

        public void OnGet()
        {
        }

        public IActionResult OnGetSystemStats()
        {
            // 1. Pending Items Logic
            string pendingPath = Path.Combine(_env.WebRootPath, "Pending");
            int pendingCount = 0;
            if (Directory.Exists(pendingPath))
            {
                pendingCount = Directory.GetFiles(pendingPath).Where(a => !a.Contains(".part") && !a.Contains("_preview") && !a.Contains("_preview.jpg") && !a.Contains(".ytdl") && !a.Contains(".json")).ToList().Count;
            }

            string storageText = "Unknown";
            int usedPercent = 0;

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(_env.ContentRootPath));

                long totalBytes = drive.TotalSize;
                long freeBytes = drive.AvailableFreeSpace;
                long usedBytes = totalBytes - freeBytes;

                if (totalBytes > 0)
                {
                    usedPercent = (int)((double)usedBytes / totalBytes * 100);
                }

                storageText = $"{FormatBytes(freeBytes)} free / {FormatBytes(totalBytes)}";
            }
            catch { }

            return new JsonResult(new
            {
                ip = _torManager.CurrentIp ?? "Initializing...",
                country = _torManager.CurrentCountry ?? "Unknown",
                isRotating = _torManager.IsRotating,
                queueCount = pendingCount,
                storage = storageText,
                storagePercent = usedPercent,
                downloadingCount = _downloadManager.GetJobs().Count(j => j.Status != "Cancelled")
            });
        }

        // --- Helper Function for Auto-Scaling Units ---
        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            if (bytes == 0) return "0 B";
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return $"{num} {suffixes[place]}";
        }
    }
}