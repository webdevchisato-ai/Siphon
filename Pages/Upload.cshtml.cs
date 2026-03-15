using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
using System;
using System.IO;
using System.Linq; // Needed for .Count()
using System.Threading.Tasks;

namespace Siphon.Pages
{
    // 1. INCREASE UPLOAD LIMITS
    // These attributes ensure the function is actually called for large files.
    [RequestSizeLimit(16_106_127_360)] //15GB
    [RequestFormLimits(MultipartBodyLengthLimit = 16_106_127_360)]
    public class UploadModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<UploadModel> _logger;
        private readonly PreviewGenerator _previewGenerator;
        private readonly ArchiverService _archiverService;

        public UploadModel(IWebHostEnvironment environment, ILogger<UploadModel> logger, PreviewGenerator previewGenerator, ArchiverService archiverService)
        {
            _env = environment;
            _logger = logger;
            _previewGenerator = previewGenerator;
            _archiverService = archiverService;
        }

        [BindProperty]
        public string TargetUrl { get; set; }

        [BindProperty]
        public IFormFile UploadFile { get; set; }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Debugging Log: Prove we entered the function
            _logger.LogInformation(">>> OnPostAsync Entered. URL: {TargetUrl}, File: {FileName}",
                TargetUrl, UploadFile?.FileName ?? "NULL");

            if (UploadFile == null || string.IsNullOrEmpty(TargetUrl))
            {
                ModelState.AddModelError("", "File and URL are required.");
                // If this hits, the JS will see a 200 OK HTML response and reload the page, 
                // making it look like 'nothing happened'.
                return Page();
            }

            var uploadsFolder = Path.Combine(_env.WebRootPath, "Pending");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            // --- PART 1: Determine Final Name ---
            var finalFilePath = Path.Combine(uploadsFolder, UploadFile.FileName);

            if (System.IO.File.Exists(finalFilePath))
            {
                int currentPendingFileCount = new DirectoryInfo(uploadsFolder).GetFiles().Count();
                _logger.LogWarning($"File already exists: {finalFilePath}. Appending index.");
                finalFilePath = Path.Combine(uploadsFolder, $"{Path.GetFileNameWithoutExtension(UploadFile.FileName)}_{currentPendingFileCount}{Path.GetExtension(UploadFile.FileName)}");
            }

            // --- PART 2: Save as .part ---
            var tempFilePath = finalFilePath + ".part";

            try
            {
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
                {
                    _logger.LogInformation("Streaming file to .part: {TempFilePath}", tempFilePath);
                    await UploadFile.CopyToAsync(fileStream);
                }

                // --- PART 3: Rename to Final ---
                _logger.LogInformation("Rename .part to final: {FinalFilePath}", finalFilePath);
                System.IO.File.Move(tempFilePath, finalFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during file save/rename.");
                ModelState.AddModelError("", "File save failed.");
                return Page();
            }

            // --- PART 4: Update Lookup JSON ---
            _logger.LogInformation("Updating Lookup JSON...");
            string pendingFilePath = Path.Combine(_env.WebRootPath, "Lookups", "PendingFileURLs.json");
            var pendingFiles = new PendingVideoUrlContainer();
            string urlAssocFileName = Path.GetFileNameWithoutExtension(finalFilePath);

            if (!Directory.Exists(Path.Combine(_env.WebRootPath, "Lookups")))
            {
                Directory.CreateDirectory(Path.Combine(_env.WebRootPath, "Lookups"));
            }

            if (System.IO.File.Exists(pendingFilePath))
            {
                pendingFiles = JsonHandler.DeserializeJsonFile<PendingVideoUrlContainer>(pendingFilePath);
            }

            if (!pendingFiles.Urls.ContainsKey(urlAssocFileName))
            {
                pendingFiles.Urls.Add(urlAssocFileName, TargetUrl);
            }

            JsonHandler.SerializeJsonFile(pendingFilePath, pendingFiles);

            // --- PART 5: Generate Preview ---
            _logger.LogInformation("Queueing preview generation...");
            _previewGenerator.QueueGeneration(finalFilePath);

            _archiverService.AddDownload(TargetUrl, urlAssocFileName, DateTime.Now, new FileInfo(finalFilePath).Length);

            return RedirectToPage();
        }
    }
}