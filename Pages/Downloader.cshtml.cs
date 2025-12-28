using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;

namespace Siphon.Pages
{
    [IgnoreAntiforgeryToken]
    public class DownloaderModel : PageModel
    {
        private readonly DownloadManager _downloadManager;
        private readonly TorProxyManager _torManager; // Injected Tor Manager
        private readonly string _configPath;

        public DownloaderModel(DownloadManager downloadManager, TorProxyManager torManager)
        {
            _downloadManager = downloadManager;
            _torManager = torManager;
            // Config location mapped in Docker
            _configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "scraper_config.txt");
        }

        public List<DownloadJob> Jobs { get; private set; } = new();

        [BindProperty]
        public string Url { get; set; }

        // --- Config Properties ---
        [BindProperty]
        public string PhpSessId { get; set; }

        [BindProperty]
        public string Eprns { get; set; }

        public void OnGet()
        {
            Jobs = _downloadManager.GetJobs().OrderByDescending(x => x.Id).ToList();
            LoadConfig();
        }

        public IActionResult OnPost()
        {
            if (!string.IsNullOrWhiteSpace(Url))
            {
                _downloadManager.QueueUrl(Url);
            }
            return RedirectToPage();
        }

        public IActionResult OnPostUpdateSettings()
        {
            SaveConfig();
            return RedirectToPage();
        }

        // --- AJAX Handlers ---

        public IActionResult OnGetStatus()
        {
            return new JsonResult(_downloadManager.GetJobs());
        }

        public IActionResult OnPostCancel(string id)
        {
            _downloadManager.CancelJob(id);
            return new JsonResult(new { success = true });
        }

        // NEW: Tor Status Handler
        public IActionResult OnGetTorStatus()
        {
            return new JsonResult(new
            {
                ip = _torManager.CurrentIp,
                country = _torManager.CurrentCountry,
                isRotating = _torManager.IsRotating
            });
        }

        // NEW: Tor Reset Handler
        public async Task<IActionResult> OnPostResetTor()
        {
            // Trigger the rebuild in background or wait for it? 
            // Better to fire and forget here or just await the signal sending.
            // We await the method which sets "IsRotating" to true immediately.
            _ = _torManager.RebuildCircuitAsync();

            return new JsonResult(new { success = true, message = "Tor circuit rebuild initiated." });
        }

        // --- Config Methods ---

        private void LoadConfig()
        {
            try
            {
                if (System.IO.File.Exists(_configPath))
                {
                    var lines = System.IO.File.ReadAllLines(_configPath);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("PHPSESSID=")) PhpSessId = line.Substring(10).Trim();
                        if (line.StartsWith("EPRNS=")) Eprns = line.Substring(6).Trim();
                    }
                }
            }
            catch { /* Ignore */ }
        }

        private void SaveConfig()
        {
            try
            {
                var lines = new List<string>
                {
                    $"PHPSESSID={PhpSessId}",
                    $"EPRNS={Eprns}",
                    "THREADS=3", // Hardcoded as managed by Semaphore
                    "PATH=/app/wwwroot/Pending" // Hardcoded container path
                };

                var dir = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                System.IO.File.WriteAllLines(_configPath, lines);
            }
            catch { /* Handle error */ }
        }
    }
}