using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
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
        public string downloadedURL { get; set; }
    }

    public class ApprovedModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ApprovedModel> _logger;

        public ApprovedModel(IWebHostEnvironment env, ILogger<ApprovedModel> logger)
        {
            _env = env;
            _logger = logger;
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
                    ApprovedDirReadable = "Approved",
                    downloadedURL = GetDownloadedFileUrl(file.Name)
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
                        ApprovedDirReadable = $"{dir.Replace("Extra_Approved", "").Replace("/", "")}",
                        downloadedURL = GetDownloadedFileUrl(file.Name)
                    });
                }
            }
        }

        private string GetDownloadedFileUrl(string fileName)
        {
            // 1. Get the base name
            string baseName = Path.GetFileNameWithoutExtension(fileName);

            // 2. Define a local helper to "normalize" strings
            //    This keeps ONLY letters and numbers and converts to lowercase.
            //    Example: "My Video (2023)" -> "myvideo2023"
            //    Example: "My.Video.2023"   -> "myvideo2023"
            string CleanString(string input)
            {
                if (string.IsNullOrEmpty(input)) return string.Empty;
                // precise filtering: only keep letters or digits
                return new string(input.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();
            }

            // 3. Prepare our search term
            string targetCleaned = CleanString(baseName);

            // 4. Load the file
            string pendingURLFile = Path.Combine(_env.WebRootPath, "Lookups", "PendingFileURLs.json");
            _logger.LogInformation($"Looking for url for {baseName} (Normalized: {targetCleaned})");

            if (!System.IO.File.Exists(pendingURLFile))
            {
                _logger.LogInformation($"Pending URL File does not exist");
                return null;
            }

            // 5. Check matches
            var pendingFileURLS = JsonHandler.DeserializeJsonFile<PendingVideoUrlContainer>(pendingURLFile);

            // We look for the FIRST key in the dictionary where the cleaned version matches our target
            string matchedKey = pendingFileURLS.Urls.Keys
                                .FirstOrDefault(k => CleanString(k) == targetCleaned);

            if (matchedKey != null)
            {
                _logger.LogInformation($"Found downloaded URL for {baseName} (matched via {matchedKey}): {pendingFileURLS.Urls[matchedKey]}");
                return pendingFileURLS.Urls[matchedKey];
            }
            else
            {
                _logger.LogInformation($"No downloaded URL found for {baseName}");
                return null;
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