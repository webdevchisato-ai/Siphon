using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Siphon.Pages
{
    public class ApprovedFile
    {
        public string Name { get; set; }
        public string Size { get; set; }
        public string Url { get; set; }
        public DateTime Created { get; set; }
    }

    public class ApprovedModel : PageModel
    {
        private readonly IWebHostEnvironment _env;

        public ApprovedModel(IWebHostEnvironment env)
        {
            _env = env;
        }

        public List<ApprovedFile> Files { get; set; } = new();

        public void OnGet()
        {
            var approvedDir = Path.Combine(_env.WebRootPath, "Approved");
            if (!Directory.Exists(approvedDir)) Directory.CreateDirectory(approvedDir);

            var dirInfo = new DirectoryInfo(approvedDir);

            // Get all files, order by newest first
            var files = dirInfo.GetFiles().OrderByDescending(f => f.CreationTime);

            foreach (var file in files)
            {
                // Skip hidden files or random non-media if you want, currently including all
                Files.Add(new ApprovedFile
                {
                    Name = file.Name,
                    Size = FormatSize(file.Length),
                    Url = $"/Approved/{file.Name}",
                    Created = file.CreationTime
                });
            }
        }

        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }
    }
}