using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Siphon.Data;
using Siphon.Models;

namespace Siphon.Services
{
    public class ArchiverService
    {
        private readonly ILogger<ArchiverService> _logger;
        private readonly IDbContextFactory<ArchiverDbContext> _dbFactory;

        public ArchiverService(ILogger<ArchiverService> logger, IDbContextFactory<ArchiverDbContext> dbFactory)
        {
            _logger = logger;
            _dbFactory = dbFactory;
        }

        public void AddDownload(string url, string fileName, DateTime? downloadDate, long? fileSize)
        {
            using var context = _dbFactory.CreateDbContext();

            var existing = context.Downloaded.FirstOrDefault(d => d.URL == url);
            if (existing != null)
            {
                _logger.LogWarning($"Download with URL {url} already exists. Skipping.");
                return;
            }

            var newDownload = new DownloadObject
            {
                URL = url,
                FileName = fileName, // Assigned here
                DownloadDate = downloadDate,
                ArchiveDate = DateTime.Now,
                FileSize = fileSize,
                IsArchived = false
            };

            context.Downloaded.Add(newDownload);
            context.SaveChanges();

            _logger.LogInformation($"Added new download record for {url}.");
        }

        public void ArchiveDownload(string url)
        {
            using var context = _dbFactory.CreateDbContext();

            var record = context.Downloaded.FirstOrDefault(d => d.URL == url);
            if (record != null)
            {
                record.IsArchived = true;
                record.ArchiveDate = DateTime.Now;

                context.SaveChanges();
                _logger.LogInformation($"Archived download record for {url}.");
            }
            else
            {
                _logger.LogWarning($"Could not find download record for {url} to archive.");
            }
        }

        public DownloadObject? GetDownload(string url)
        {
            using var context = _dbFactory.CreateDbContext();
            return context.Downloaded.FirstOrDefault(d => d.URL == url);
        }

        public void UpdateDownload(string url, DownloadObject download)
        {
            using var context = _dbFactory.CreateDbContext();

            var record = context.Downloaded.FirstOrDefault(d => d.URL == url);
            if (record != null)
            {
                record.DownloadDate = download.DownloadDate;
                record.ArchiveDate = download.ArchiveDate;
                record.FileSize = download.FileSize;
                record.IsArchived = download.IsArchived;

                context.SaveChanges();
                _logger.LogInformation($"Updated download record for {url}.");
            }
            else
            {
                _logger.LogWarning($"Could not find download record for {url} to update.");
            }
        }

        public List<DownloadObject> GetArchivedDownloads()
        {
            using var context = _dbFactory.CreateDbContext();
            return context.Downloaded.Where(d => d.IsArchived).ToList();
        }

        public void RemoveDownloadArchive(string url)
        {
            using var context = _dbFactory.CreateDbContext();

            var record = context.Downloaded.FirstOrDefault(d => d.URL == url);
            if (record != null)
            {
                context.Downloaded.Remove(record);
                context.SaveChanges();
                _logger.LogInformation($"Removed download record for {url}.");
            }
            else
            {
                _logger.LogWarning($"Could not find download record for {url} to remove.");
            }
        }

        public bool DownloadExists(string url)
        {
            using var context = _dbFactory.CreateDbContext();
            return context.Downloaded.Any(d => d.URL == url && d.IsArchived);
        }
    }
}