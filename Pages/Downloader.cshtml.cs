using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;

namespace Siphon.Pages
{
    [IgnoreAntiforgeryToken]
    public class DownloaderModel : PageModel
    {
        private readonly DownloadManager _downloadManager;
        private readonly TorProxyManager _torManager;
        private readonly string _configPath;

        public DownloaderModel(DownloadManager downloadManager, TorProxyManager torManager)
        {
            _downloadManager = downloadManager;
            _torManager = torManager;
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

        [BindProperty]
        public int Threads { get; set; } = 3; // Default

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

        // NEW: Handler for Hot Reload Button
        public IActionResult OnPostReloadConfig()
        {
            // Trigger logic in Manager (reloads threads)
            _downloadManager.ReloadConfig();
            return new JsonResult(new { success = true, message = "Configuration reloaded successfully." });
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

        public IActionResult OnGetTorStatus()
        {
            return new JsonResult(new
            {
                ip = _torManager.CurrentIp,
                country = _torManager.CurrentCountry,
                isRotating = _torManager.IsRotating
            });
        }

        public async Task<IActionResult> OnPostResetTor()
        {
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
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("PHPSESSID=")) PhpSessId = trimmed.Substring(10).Trim();
                        if (trimmed.StartsWith("EPRNS=")) Eprns = trimmed.Substring(6).Trim();
                        if (trimmed.StartsWith("THREADS="))
                        {
                            if (int.TryParse(trimmed.Substring(8).Trim(), out int t)) Threads = t;
                        }
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
                    $"THREADS={Threads}",
                    "PATH=/app/wwwroot/Pending"
                };

                var dir = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                System.IO.File.WriteAllLines(_configPath, lines);
            }
            catch { /* Handle error */ }
        }
    }
}