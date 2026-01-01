using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;

namespace Siphon.Pages
{
    public class ApprovedFile
    {
        public string Name { get; set; }
        public string Size { get; set; }
        public string Url { get; set; }
        public DateTime Created { get; set; }
        public string ApprovedDirReadable { get; set; }
    }

    public class ApprovedModel : PageModel
    {
        private readonly IWebHostEnvironment _env;

        public ApprovedModel(IWebHostEnvironment env)
        {
            _env = env;
        }

        public List<ApprovedFile> Files { get; set; } = new();
        private List<string> ApprovalDirectories { get; set; } = new();

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
                    Created = file.CreationTime,
                    ApprovedDirReadable = "Approved"
                });
            }

            var configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "extra_dirs.json");
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
                }
                catch { }
            }

            foreach (var dir in ApprovalDirectories)
            {
                var extraDirInfo = new DirectoryInfo(Path.Combine(_env.WebRootPath, dir));
                var extraFiles = extraDirInfo.GetFiles().OrderByDescending(f => f.CreationTime);

                foreach (var file in extraFiles)
                {
                    Files.Add(new ApprovedFile
                    {
                        Name = file.Name,
                        Size = FormatSize(file.Length),
                        Url = $"/{dir}/{file.Name}",
                        Created = file.CreationTime,
                        ApprovedDirReadable = $"{dir.Replace("Extra_Approved", "").Replace("/", "")}"
                    });
                }
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