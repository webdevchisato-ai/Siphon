using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Siphon.Pages
{
    public class SettingsModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly UserService _userService;
        private readonly DownloadManager _downloadManager;
        private readonly RetentionService _retentionService;
        private readonly string _dirsConfigPath;

        public SettingsModel(
            IWebHostEnvironment env,
            UserService userService,
            DownloadManager downloadManager,
            RetentionService retentionService)
        {
            _env = env;
            _userService = userService;
            _downloadManager = downloadManager;
            _retentionService = retentionService;
            _dirsConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "extra_dirs.json");
        }

        [BindProperty] public string Username { get; set; }
        [BindProperty] public string Password { get; set; }
        [BindProperty] public int RetentionValue { get; set; }
        [BindProperty] public string RetentionUnit { get; set; }
        [BindProperty] public List<string> ExtraDirectories { get; set; } = new();

        public void OnGet()
        {
            // 1. Load User Config
            var userConfig = _userService.GetUserConfig();
            if (userConfig != null)
            {
                Username = userConfig.Username;
                if (userConfig.PendingPreservationMinutes % 525600 == 0) { RetentionValue = userConfig.PendingPreservationMinutes / 525600; RetentionUnit = "Years"; }
                else if (userConfig.PendingPreservationMinutes % 43200 == 0) { RetentionValue = userConfig.PendingPreservationMinutes / 43200; RetentionUnit = "Months"; }
                else if (userConfig.PendingPreservationMinutes % 10080 == 0) { RetentionValue = userConfig.PendingPreservationMinutes / 10080; RetentionUnit = "Weeks"; }
                else if (userConfig.PendingPreservationMinutes % 1440 == 0) { RetentionValue = userConfig.PendingPreservationMinutes / 1440; RetentionUnit = "Days"; }
                else if (userConfig.PendingPreservationMinutes % 60 == 0) { RetentionValue = userConfig.PendingPreservationMinutes / 60; RetentionUnit = "Hours"; }
                else { RetentionValue = userConfig.PendingPreservationMinutes; RetentionUnit = "Minutes"; }
            }

            // 2. Load Existing Directories
            if (System.IO.File.Exists(_dirsConfigPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(_dirsConfigPath);
                    ExtraDirectories = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                }
                catch { }
            }
        }

        public IActionResult OnPost()
        {
            // 1. Calculate Minutes
            int minutes = RetentionValue;
            switch (RetentionUnit)
            {
                case "Hours": minutes *= 60; break;
                case "Days": minutes *= 1440; break;
                case "Weeks": minutes *= 10080; break;
                case "Months": minutes *= 43200; break;
                case "Years": minutes *= 525600; break;
            }

            // 2. Update User Config
            _userService.UpdateConfiguration(Username, Password, minutes);

            // 3. Handle Directory Logic (Create New & Delete Removed)

            // A. Sanitize Input List
            var newDirs = ExtraDirectories
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => SanitizeDirName(d))
                .Distinct()
                .ToList();

            // B. Load Old List (to find what was removed)
            var oldDirs = new List<string>();
            if (System.IO.File.Exists(_dirsConfigPath))
            {
                try
                {
                    oldDirs = JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(_dirsConfigPath)) ?? new List<string>();
                }
                catch { }
            }

            // C. Save New Config
            var configDir = Path.GetDirectoryName(_dirsConfigPath);
            if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
            System.IO.File.WriteAllText(_dirsConfigPath, JsonSerializer.Serialize(newDirs));

            // D. Create New Directories
            foreach (var dir in newDirs)
            {
                var path = Path.Combine(_env.WebRootPath, "Extra_Approved", dir);
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            }

            // E. Delete Removed Directories
            // Identify directories present in OldDirs but NOT in NewDirs
            var removedDirs = oldDirs.Except(newDirs).ToList();
            foreach (var dir in removedDirs)
            {
                var path = Path.Combine(_env.WebRootPath, "Extra_Approved", dir);
                if (Directory.Exists(path))
                {
                    try
                    {
                        // 'true' means recursive delete. Be careful: this deletes all files inside!
                        Directory.Delete(path, true);
                    }
                    catch { /* Handle permission errors or locked files */ }
                }
            }

            // 4. Broadcast Updates
            _retentionService.UpdateTimerInterval();
            _downloadManager.ReloadConfig();

            TempData["Message"] = "Settings saved. Directories updated.";
            return RedirectToPage();
        }

        private string SanitizeDirName(string name)
        {
            // Remove invalid chars to prevent "Path not found" errors
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRe = string.Format(@"[{0}]+", invalidChars);
            return Regex.Replace(name, invalidRe, "_").Trim();
        }
    }
}