using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        public void OnGet()
        {
            var archivedDownloads = _archiverService.GetArchivedDownloads();

            Files = archivedDownloads.Select(d => new ArchiveItem
            {
                // UPDATED: Now pulling directly from the database FileName
                Name = string.IsNullOrWhiteSpace(d.FileName) ? $"Archived_File_{d.Id}" : d.FileName,
                DownloadDateTime = d.DownloadDate ?? d.ArchiveDate,
                FileSize = d.FileSize,
                URL = d.URL
            }).ToList();
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
    }
}