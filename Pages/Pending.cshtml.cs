using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
using System.Diagnostics;
using System.Text.Json;

namespace Siphon.Pages
{
    public class PendingFile
    {
        public string Name { get; set; }
        public string OriginalVideoUrl { get; set; }
        public string PreviewVideoUrl { get; set; }
        public string ThumbPath { get; set; }
        public string FullPath { get; set; }
        public bool IsProcessing { get; set; }
        public List<int> VolumeData { get; set; } = new();
        public string downloadedURL { get; set; }
        public double videoLenghtSeconds { get; set; }
    }

    [IgnoreAntiforgeryToken]
    public class PendingModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly PreviewGenerator _previewGenerator;
        private readonly UserService _userService;
        private readonly ILogger<PendingModel> _logger;

        private static Dictionary<string, DateTime> oldPendingFiles; //file name, time added

        public PendingModel(IWebHostEnvironment env, PreviewGenerator previewGenerator, UserService userService, ILogger<PendingModel> logger)
        {
            _env = env;
            _previewGenerator = previewGenerator;
            _userService = userService;
            _logger = logger;
        }

        public List<PendingFile> Files { get; set; } = new();

        // List to hold our approval options
        public List<string> ApprovalDirectories { get; set; } = new();

        public async Task OnGetAsync()
        {
            LoadApprovalDirectories();
            await LoadFiles();
            foreach (var key in oldPendingFiles.Keys.ToList())
            {
                if ((DateTime.Now - oldPendingFiles[key]).TotalSeconds > 30)
                {
                    oldPendingFiles.Remove(key);
                }
            }
        }

        // Updated handler accepts targetDir
        public IActionResult OnPostApprove(string fileName, string targetDir)
        {
            // 1. Capture the variables we need so they are safe to use in the background thread
            string currentFileName = fileName;
            string currentTarget = targetDir;
            oldPendingFiles.Add(fileName, DateTime.Now);
            // 2. Run the file operations in a background thread
            Task.Run(() =>
            {
                try
                {
                    // We call MoveFile here. Since _env is a singleton service, 
                    // it is safe to access even after the request ends.
                    MoveFile(currentFileName, currentTarget);
                }
                catch (Exception ex)
                {
                    // OPTIONAL: Log error here since there is no UI to show it to
                    // _logger.LogError(ex, "Failed to move file in background");
                }
            });

            // 3. Return immediately. The user sees the page refresh instantly.
            return RedirectToPage();
        }

        public IActionResult OnPostDeny(string fileName)
        {
            string currentFile = fileName;
            oldPendingFiles.Add(fileName, DateTime.Now);
            // Fire and forget the delete operation
            Task.Run(() =>
            {
                DeleteFileSet(currentFile);
            });

            return RedirectToPage();
        }

        private void LoadApprovalDirectories()
        {
            // Always add the default
            ApprovalDirectories.Add(_userService.GetDefaultApprovedDirName());

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
        }

        private async Task LoadFiles()
        {
            // ... (Same logic as your existing OnGet file loading) ...
            // [I omitted the repeated code for brevity, insert your existing file loading logic here]
            if (oldPendingFiles == null)
            {
                oldPendingFiles = new Dictionary<string, DateTime>();
            }

            var pendingDir = Path.Combine(_env.WebRootPath, "Pending");
            if (!Directory.Exists(pendingDir)) Directory.CreateDirectory(pendingDir);

            var dirInfo = new DirectoryInfo(pendingDir);
            var files = dirInfo.GetFiles("*.mp4")
                               .Where(f => !f.Name.EndsWith("_preview.mp4"))
                               .OrderByDescending(f => f.CreationTime);
            

            foreach (var file in files)
            {
                string baseName = Path.GetFileNameWithoutExtension(file.Name);
                string thumbName = baseName + "_preview.jpg";
                string previewName = baseName + "_preview.mp4";
                string thumbPath = Path.Combine(pendingDir, thumbName);
                string previewPath = Path.Combine(pendingDir, previewName);
                string jsonName = baseName + ".json";
                string jsonPath = Path.Combine(pendingDir, jsonName);

                List<int> volumeData = new();
                if (System.IO.File.Exists(jsonPath))
                {
                    try
                    {
                        var jsonContent = System.IO.File.ReadAllText(jsonPath);
                        volumeData = JsonSerializer.Deserialize<List<int>>(jsonContent) ?? new();
                    }
                    catch { /* ignore read errors */ }
                }

                bool isProcessing = _previewGenerator.IsProcessing(file.FullName);
                bool assetsMissing = false;

                if (_userService.GetGenerateHeatmapStatus())
                {
                    assetsMissing = !System.IO.File.Exists(thumbPath) || !System.IO.File.Exists(previewPath) || !System.IO.File.Exists(jsonPath);
                }
                else
                {
                    assetsMissing = !System.IO.File.Exists(thumbPath) || !System.IO.File.Exists(previewPath);
                }

                if (assetsMissing && !isProcessing && !file.Name.Contains(".part"))
                {
                    _previewGenerator.QueueGeneration(file.FullName);
                    isProcessing = true;
                }

                if (!oldPendingFiles.Keys.Contains(file.Name))
                {
                    if (_userService.GetGenerateHeatmapStatus())
                    {
                        Files.Add(new PendingFile
                        {
                            Name = file.Name,
                            FullPath = file.FullName,
                            OriginalVideoUrl = $"/Pending/{file.Name}",
                            PreviewVideoUrl = $"/Pending/{previewName}",
                            ThumbPath = $"/Pending/{thumbName}",
                            IsProcessing = isProcessing,
                            VolumeData = volumeData,
                            downloadedURL = GetDownloadedFileUrl(file.Name),
                            videoLenghtSeconds = await GetVideoDuration(file.FullName)
                        });
                    }
                    else
                    {
                        Files.Add(new PendingFile
                        {
                            Name = file.Name,
                            FullPath = file.FullName,
                            OriginalVideoUrl = $"/Pending/{file.Name}",
                            PreviewVideoUrl = $"/Pending/{previewName}",
                            ThumbPath = $"/Pending/{thumbName}",
                            IsProcessing = isProcessing,
                            downloadedURL = GetDownloadedFileUrl(file.Name),
                            videoLenghtSeconds = await GetVideoDuration(file.FullName)
                        });
                    }
                }
            }
        }

        private async Task<double> GetVideoDuration(string filePath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                // This command returns ONLY the duration in seconds (e.g., 12.500000)
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (double.TryParse(output.Trim(), out double seconds))
            {
                return seconds;
            }

            return 0;
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

        private void MoveFile(string fileName, string targetFolder)
        {
            // Default fallback
            if (string.IsNullOrEmpty(targetFolder)) targetFolder = "Approved";

            var srcPath = Path.Combine(_env.WebRootPath, "Pending", fileName);
            // Construct destination based on whether it is a root folder or nested
            var destPath = Path.Combine(_env.WebRootPath, targetFolder, fileName);

            // Ensure target dir exists (just in case)
            var destDir = Path.GetDirectoryName(destPath);
            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            if (System.IO.File.Exists(srcPath))
            {
                System.IO.File.Move(srcPath, destPath);
                CleanupExtras(srcPath);
            }
        }

        private void DeleteFileSet(string fileName)
        {
            var path = Path.Combine(_env.WebRootPath, "Pending", fileName);
            try { System.IO.File.Delete(path); } catch { }
            CleanupExtras(path);
        }

        private void CleanupExtras(string originalPath)
        {
            var thumb = originalPath.Replace(".mp4", "_preview.jpg");
            var preview = originalPath.Replace(".mp4", "_preview.mp4");
            var heatmap = originalPath.Replace(".mp4", ".json");

            try { System.IO.File.Delete(thumb); } catch { }
            try { System.IO.File.Delete(preview); } catch { }
            try { System.IO.File.Delete(heatmap); } catch { }
        }
    }
}