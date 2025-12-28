using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
using System.IO;
using System.Linq;

namespace Siphon.Pages
{
    public class PendingFile
    {
        public string Name { get; set; }
        public string OriginalVideoUrl { get; set; } // The full file
        public string PreviewVideoUrl { get; set; }  // The 9-second montage
        public string ThumbPath { get; set; }        // The JPG thumbnail
        public string FullPath { get; set; }
        public bool IsProcessing { get; set; }       // Controls the UI spinner
    }

    [IgnoreAntiforgeryToken]
    public class PendingModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly PreviewGenerator _previewGenerator;

        public PendingModel(IWebHostEnvironment env, PreviewGenerator previewGenerator)
        {
            _env = env;
            _previewGenerator = previewGenerator;
        }

        public List<PendingFile> Files { get; set; } = new();

        public void OnGet()
        {
            var pendingDir = Path.Combine(_env.WebRootPath, "Pending");
            var approvedDir = Path.Combine(_env.WebRootPath, "Approved");

            // Ensure directories exist
            if (!Directory.Exists(pendingDir)) Directory.CreateDirectory(pendingDir);
            if (!Directory.Exists(approvedDir)) Directory.CreateDirectory(approvedDir);

            var dirInfo = new DirectoryInfo(pendingDir);

            // Get all MP4s, excluding the generated previews
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

                // Check status from the Singleton service
                bool isProcessing = _previewGenerator.IsProcessing(file.FullName);
                bool assetsMissing = !System.IO.File.Exists(thumbPath) || !System.IO.File.Exists(previewPath);

                // FALLBACK: If assets are missing and it's NOT currently processing, start it now.
                // This handles cases where the app restarted before finishing, or a file was added manually.
                if (assetsMissing && !isProcessing)
                {
                    _previewGenerator.QueueGeneration(file.FullName);
                    isProcessing = true; // Mark as processing for the UI this time
                }

                Files.Add(new PendingFile
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    OriginalVideoUrl = $"/Pending/{file.Name}",
                    PreviewVideoUrl = $"/Pending/{previewName}",
                    ThumbPath = $"/Pending/{thumbName}",
                    IsProcessing = isProcessing
                });
            }
        }

        public IActionResult OnPostApprove(string fileName)
        {
            MoveFile(fileName, "Approved");
            return RedirectToPage();
        }

        public IActionResult OnPostDeny(string fileName)
        {
            DeleteFileSet(fileName);
            return RedirectToPage();
        }

        private void MoveFile(string fileName, string destFolder)
        {
            var srcPath = Path.Combine(_env.WebRootPath, "Pending", fileName);
            var destPath = Path.Combine(_env.WebRootPath, destFolder, fileName);

            if (System.IO.File.Exists(srcPath))
            {
                // Move the main video file
                System.IO.File.Move(srcPath, destPath);

                // Cleanup thumbnail and preview from Pending (we don't need them in Approved usually)
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
            // Delete .jpg and _preview.mp4
            var thumb = Path.ChangeExtension(originalPath, ".jpg");
            var preview = originalPath.Replace(".mp4", "_preview.mp4");

            if (System.IO.File.Exists(thumb)) System.IO.File.Delete(thumb);
            if (System.IO.File.Exists(preview)) System.IO.File.Delete(preview);
        }
    }
}