using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
using System;
using System.Data;

namespace Siphon.Pages
{
    [IgnoreAntiforgeryToken]
    public class DownloaderModel : PageModel
    {
        private readonly DownloadManager _downloadManager;
        private readonly TorProxyManager _torManager;
        private readonly ArchiverService _archiverService;
        private readonly string _configPath;

        public DownloaderModel(DownloadManager downloadManager, TorProxyManager torManager, ArchiverService archiverService)
        {
            _downloadManager = downloadManager;
            _torManager = torManager;
            _archiverService = archiverService;
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
        public string CoomerSession { get; set; }

        [BindProperty]
        public string KemonoSession { get; set; }

        [BindProperty]
        public string CF_CLEARANCE { get; set; }

        [BindProperty]
        public string MANGAVIEW_COOKIE { get; set; }

        [BindProperty]
        public string HentaiDudeAgent { get; set; }

        [BindProperty]
        public int Threads { get; set; } = 3; // Default
        [BindProperty] public string RULE34APIKEY { get; set; }
        [BindProperty] public string RULE34USERID { get; set; }
        [BindProperty] public string REDDITCOOKIE { get; set; }

        public void OnGet()
        {
            Jobs = _downloadManager.GetJobs().OrderByDescending(x => x.Id).ToList();
            LoadConfig();
        }

        // UPDATED: Now accepts a Force parameter to override the archive
        public IActionResult OnPost(bool Force = false)
        {
            if (!string.IsNullOrWhiteSpace(Url))
            {
                if (Force)
                {
                    _archiverService.RemoveDownloadArchive(Url);
                }
                _downloadManager.QueueUrl(Url);
            }
            return RedirectToPage();
        }

        public IActionResult OnPostUpdateSettings()
        {
            SaveConfig();
            return RedirectToPage();
        }

        public IActionResult OnPostReloadConfig()
        {
            // Trigger logic in Manager (reloads threads)
            _downloadManager.ReloadConfig();
            return new JsonResult(new { success = true, message = "Configuration reloaded successfully." });
        }

        // NEW: Endpoint to let the frontend quickly check the archive state
        public IActionResult OnGetCheckArchive(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return new JsonResult(new { isArchived = false });

            var isArchived = _archiverService.DownloadExists(url);
            if (isArchived)
            {
                var record = _archiverService.GetDownload(url);
                return new JsonResult(new
                {
                    isArchived = true,
                    filename = string.IsNullOrWhiteSpace(record.FileName) ? $"Archived_File_{record.Id}" : record.FileName,
                    date = record.ArchiveDate.ToString("g"),
                    size = FormatBytes(record.FileSize)
                });
            }

            return new JsonResult(new { isArchived = false });
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

        // --- Helper Methods ---

        private string FormatBytes(long? bytes)
        {
            if (!bytes.HasValue) return "Unknown Size";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            if (bytes.Value == 0) return "0 B";

            long bytesValue = Math.Abs(bytes.Value);
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytesValue, 1024)));
            double num = Math.Round(bytesValue / Math.Pow(1024, place), 1);

            return (Math.Sign(bytes.Value) * num).ToString() + " " + suffixes[place];
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
                        if (trimmed.StartsWith("COOMER_SESSION=")) CoomerSession = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("KEMONO_SESSION=")) KemonoSession = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("CF_CLEARANCE=")) CF_CLEARANCE = trimmed.Substring(13).Trim();
                        if (trimmed.StartsWith("MANGAVIEW_COOKIE=")) MANGAVIEW_COOKIE = trimmed.Substring(17).Trim();
                        if (trimmed.StartsWith("HENTAI_DUDE_AGENT=")) HentaiDudeAgent = trimmed.Substring(18).Trim();
                        if (trimmed.StartsWith("RULE34_USER_ID=")) RULE34USERID = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("RULE34_API_KEY=")) RULE34APIKEY = trimmed.Substring(15).Trim();
                        if (trimmed.StartsWith("REDDIT_COOKIE=")) REDDITCOOKIE = trimmed.Substring(14).Trim();
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
                    $"COOMER_SESSION={CoomerSession}",
                    $"KEMONO_SESSION={KemonoSession}",
                    $"THREADS={Threads}",
                    $"CF_CLEARANCE={CF_CLEARANCE}",
                    $"MANGAVIEW_COOKIE={MANGAVIEW_COOKIE}",
                    $"HENTAI_DUDE_AGENT={HentaiDudeAgent}",
                    $"RULE34_USER_ID={RULE34USERID}",
                    $"RULE34_API_KEY={RULE34APIKEY}",
                    $"REDDIT_COOKIE={REDDITCOOKIE}",
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