using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Siphon.Pages
{
    public class SetupModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly UserService _userService;

        public SetupModel(IWebHostEnvironment env, UserService userService)
        {
            _env = env;
            _userService = userService;
        }

        [BindProperty] public string Username { get; set; }
        [BindProperty] public string Password { get; set; }
        [BindProperty] public int RetentionValue { get; set; } = 24;
        [BindProperty] public string RetentionUnit { get; set; } = "Hours";
        [BindProperty] public List<string> ExtraDirectories { get; set; } = new();
        [BindProperty] public int ApprovedRetentionValue { get; set; } = 1;
        [BindProperty] public string ApprovedRetentionUnit { get; set; } = "Hours";

        public IActionResult OnGet()
        {
            if (!_userService.IsSetupRequired()) return RedirectToPage("/Index");
            return Page();
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid) return Page();

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

            int approvedMinutes = ApprovedRetentionValue;
            switch (ApprovedRetentionUnit)
            {
                case "Hours": approvedMinutes *= 60; break;
                case "Days": approvedMinutes *= 1440; break;
                case "Weeks": approvedMinutes *= 10080; break;
                case "Months": approvedMinutes *= 43200; break;
                case "Years": approvedMinutes *= 525600; break;
            }

            // 2. Create User
            _userService.CreateUser(Username, Password, minutes, approvedMinutes);

            // 3. Sanitize and Save Directories
            var cleanDirs = ExtraDirectories
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => SanitizeDirName(d)) // Apply sanitization
                .Distinct()
                .ToList();

            var dirsPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "extra_dirs.json");
            var configDir = Path.GetDirectoryName(dirsPath);
            if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);

            System.IO.File.WriteAllText(dirsPath, JsonSerializer.Serialize(cleanDirs));

            // 4. Create Physical Directories
            foreach (var dir in cleanDirs)
            {
                var path = Path.Combine(_env.WebRootPath, "Extra_Approved", dir);
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            }

            // Ensure default folders exist
            Directory.CreateDirectory(Path.Combine(_env.WebRootPath, "Pending"));
            Directory.CreateDirectory(Path.Combine(_env.WebRootPath, "Approved"));

            return RedirectToPage("/Index");
        }

        private string SanitizeDirName(string name)
        {
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRe = string.Format(@"[{0}]+", invalidChars);
            return Regex.Replace(name, invalidRe, "_").Trim();
        }
    }
}