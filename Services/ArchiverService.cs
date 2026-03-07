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

        public List<DownloadObject> GetArchiveContents()
        {
            using var context = _dbFactory.CreateDbContext();
            return context.Downloaded.ToList();
        }

        public List<DownloadObject> GetPaginatedArchiveContents(int pageNumber, int pageSize)
        {
            using var context = _dbFactory.CreateDbContext();
            return context.Downloaded
                .AsNoTracking() // Huge performance boost for read-only data
                .OrderByDescending(d => d.ArchiveDate) // Show newest first
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList();
        }

        // NEW: Get total count for pagination math
        public int GetTotalArchiveCount()
        {
            using var context = _dbFactory.CreateDbContext();
            return context.Downloaded.Count();
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

        public void SeedTestData(int count = 500)
        {
            using var context = _dbFactory.CreateDbContext();

            // Don't seed if we already have test data to avoid duplicating 500 items over and over
            if (context.Downloaded.Count() >= count) return;

            var testRecords = new List<DownloadObject>();
            var rand = new Random();

            for (int i = 1; i <= count; i++)
            {
                testRecords.Add(new DownloadObject
                {
                    URL = $"https://example.com/video/fake_download_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4",
                    FileName = $"Test_Video_File_{i}.mp4",
                    DownloadDate = DateTime.Now.AddDays(-rand.Next(1, 365)), // Random date in the last year
                    ArchiveDate = DateTime.Now.AddDays(-rand.Next(1, 30)),   // Random date in the last month
                    FileSize = (long)(rand.NextDouble() * 1024 * 1024 * 500), // Random size up to 500MB
                    IsArchived = rand.Next(0, 2) == 1 // 50/50 chance of being true or false
                });
            }

            context.Downloaded.AddRange(testRecords);
            context.SaveChanges();
            _logger.LogInformation($"Successfully seeded {count} test records into the database.");
        }
    }
}