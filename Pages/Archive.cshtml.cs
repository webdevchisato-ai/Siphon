using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Siphon.Models;
using Siphon.Services;

namespace Siphon.Pages
{
    public class ArchiveModel : PageModel
    {
        private readonly ArchiverService _archiverService;

        public ArchiveModel(ArchiverService archiverService)
        {
            _archiverService = archiverService;
        }

        public List<ArchiveItem> Files { get; set; } = new List<ArchiveItem>();

        // Pagination Properties
        [BindProperty(SupportsGet = true)]
        public int P { get; set; } = 1;
        public int TotalPages { get; set; }
        public int TotalRecords { get; set; }
        public const int PageSize = 100;

        public void OnGet()
        {
            // Safety check for invalid page numbers
            if (P < 1) P = 1;

            TotalRecords = _archiverService.GetTotalArchiveCount();
            TotalPages = (int)Math.Ceiling(TotalRecords / (double)PageSize);

            // Fetch only the 100 records for the current page
            var archivedDownloads = _archiverService.GetPaginatedArchiveContents(P, PageSize);

            Files = archivedDownloads.Select(d => new ArchiveItem
            {
                Name = string.IsNullOrWhiteSpace(d.FileName) ? $"Archived_File_{d.Id}" : d.FileName,
                DownloadDateTime = d.DownloadDate ?? d.ArchiveDate,
                FileSize = d.FileSize,
                URL = d.URL,
                IsArchived = d.IsArchived
            }).ToList();
        }

        public IActionResult OnPostDelete(string url, int p)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                _archiverService.RemoveDownloadArchive(url);
            }

            // Redirect back to the same page number we were just on
            return RedirectToPage(new { p = p });
        }

        public string FormatBytes(long? bytes)
        {
            if (!bytes.HasValue) return "Unknown Size";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            if (bytes.Value == 0) return "0 B";

            long bytesValue = Math.Abs(bytes.Value);
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytesValue, 1024)));
            double num = Math.Round(bytesValue / Math.Pow(1024, place), 1);

            return (Math.Sign(bytes.Value) * num).ToString() + " " + suffixes[place];
        }
    }

    public class ArchiveItem
    {
        public string Name { get; set; } = string.Empty;
        public DateTime DownloadDateTime { get; set; }
        public long? FileSize { get; set; }
        public string URL { get; set; } = string.Empty;
        public bool IsArchived { get; set; }
    }
}