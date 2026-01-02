using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
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
    }

    [IgnoreAntiforgeryToken]
    public class PendingModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly PreviewGenerator _previewGenerator;
        private readonly UserService _userService;

        public PendingModel(IWebHostEnvironment env, PreviewGenerator previewGenerator, UserService userService)
        {
            _env = env;
            _previewGenerator = previewGenerator;
            _userService = userService;
        }

        public List<PendingFile> Files { get; set; } = new();

        // List to hold our approval options
        public List<string> ApprovalDirectories { get; set; } = new();

        public void OnGet()
        {
            LoadApprovalDirectories();
            LoadFiles();
        }

        // Updated handler accepts targetDir
        public IActionResult OnPostApprove(string fileName, string targetDir)
        {
            MoveFile(fileName, targetDir);
            return RedirectToPage();
        }

        public IActionResult OnPostDeny(string fileName)
        {
            DeleteFileSet(fileName);
            return RedirectToPage();
        }

        private void LoadApprovalDirectories()
        {
            // Always add the default
            ApprovalDirectories.Add("Approved");

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

        private void LoadFiles()
        {
            // ... (Same logic as your existing OnGet file loading) ...
            // [I omitted the repeated code for brevity, insert your existing file loading logic here]

            var pendingDir = Path.Combine(_env.WebRootPath, "Pending");
            if (!Directory.Exists(pendingDir)) Directory.CreateDirectory(pendingDir);

            var dirInfo = new DirectoryInfo(pendingDir);
            var files = dirInfo.GetFiles("*.mp4")
                               .Where(f => !f.Name.EndsWith("_preview.mp4"))
                               .OrderByDescending(f => f.CreationTime);

            foreach (var file in files)
            {
                string baseName = Path.GetFileNameWithoutExtension(file.Name);
                string thumbName = baseName + ".jpg";
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
                        VolumeData = volumeData
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
                    });
                }
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
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            CleanupExtras(path);
        }

        private void CleanupExtras(string originalPath)
        {
            var thumb = Path.ChangeExtension(originalPath, ".jpg");
            var preview = originalPath.Replace(".mp4", "_preview.mp4");
            var heatmap = originalPath.Replace(".mp4", ".json");
            if (System.IO.File.Exists(thumb)) System.IO.File.Delete(thumb);
            if (System.IO.File.Exists(preview)) System.IO.File.Delete(preview);
            if (System.IO.File.Exists(heatmap)) System.IO.File.Delete(heatmap);
        }
    }
}