using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Services;
using System;
using System.IO;

namespace Siphon.Pages
{
    public class UploadModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<UploadModel> _logger;
        private readonly PreviewGenerator _previewGenerator;

        public UploadModel(IWebHostEnvironment environment, ILogger<UploadModel> logger, PreviewGenerator previewGenerator)
        {
            _env = environment;
            _logger = logger;
            _previewGenerator = previewGenerator;
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
            if (UploadFile == null || string.IsNullOrEmpty(TargetUrl))
            {
                ModelState.AddModelError("", "File and URL are required.");
                return Page();
            }

            _logger.LogInformation("Received file upload: {FileName} for URL: {TargetUrl}", UploadFile.FileName, TargetUrl);

            var uploadsFolder = Path.Combine(_env.WebRootPath, "Pending");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            var filePath = Path.Combine(uploadsFolder, UploadFile.FileName);

            // 2. Save the file locally
            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                _logger.LogInformation("Saving file to: {FilePath}", filePath);
                await UploadFile.CopyToAsync(fileStream);
                _logger.LogInformation("File saved successfully.");
            }

            _logger.LogInformation("Associating file with URL: {TargetUrl}", TargetUrl);

            string pendingFilePath = Path.Combine(_env.WebRootPath, "Lookups", "PendingFileURLs.json");
            var pendingFiles = new PendingVideoUrlContainer();

            string urlAssocFileName = Path.GetFileNameWithoutExtension(filePath);

            if (!Directory.Exists(Path.Combine(_env.WebRootPath, "Lookups")))
            {
                Directory.CreateDirectory(Path.Combine(_env.WebRootPath, "Lookups"));
            }

            if (!System.IO.File.Exists(pendingFilePath))
            {
                pendingFiles.Urls.Add(urlAssocFileName, TargetUrl);
            }
            else
            {
                pendingFiles = JsonHandler.DeserializeJsonFile<PendingVideoUrlContainer>(pendingFilePath);

                if (!pendingFiles.Urls.ContainsKey(urlAssocFileName))
                {
                    pendingFiles.Urls.Add(urlAssocFileName, TargetUrl);
                }
            }

            JsonHandler.SerializeJsonFile(pendingFilePath, pendingFiles);

            _logger.LogInformation("Starting preview generation for file: {FilePath}", filePath);
            _previewGenerator.QueueGeneration(filePath);

            return RedirectToPage();
        }
    }
}