using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;

namespace Siphon.Pages
{
    [IgnoreAntiforgeryToken]
    public class SetupModel : PageModel
    {
        private readonly UserService _userService;

        public SetupModel(UserService userService)
        {
            _userService = userService;
        }

        [BindProperty]
        public string Username { get; set; } = "Admin";

        [BindProperty]
        public string Password { get; set; }

        // --- Retention Settings ---
        [BindProperty]
        public int RetentionValue { get; set; } = 2; // Default 2

        [BindProperty]
        public string RetentionUnit { get; set; } = "Days"; // Default Days

        public void OnGet()
        {
        }

        public IActionResult OnPost()
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                ModelState.AddModelError("", "Username and Password are required.");
                return Page();
            }

            // Convert Unit to Minutes
            int minutes = 0;
            switch (RetentionUnit)
            {
                case "Minutes": minutes = RetentionValue; break;
                case "Hours": minutes = RetentionValue * 60; break;
                case "Days": minutes = RetentionValue * 1440; break;
                case "Weeks": minutes = RetentionValue * 10080; break;
                case "Months": minutes = RetentionValue * 43200; break; // Approx 30 days
                case "Years": minutes = RetentionValue * 525600; break;
            }

            _userService.CreateUser(Username, Password, minutes);
            return RedirectToPage("/Login");
        }
    }
}