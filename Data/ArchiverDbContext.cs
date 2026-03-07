using Microsoft.EntityFrameworkCore;
using Siphon.Models;

namespace Siphon.Data
{
    public class ArchiverDbContext : DbContext
    {
        public ArchiverDbContext(DbContextOptions<ArchiverDbContext> options) : base(options)
        {
        }

        public DbSet<DownloadObject> Downloaded { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Making URL unique so we can easily query and prevent duplicates
            modelBuilder.Entity<DownloadObject>()
                .HasIndex(d => d.URL)
                .IsUnique();

            base.OnModelCreating(modelBuilder);
        }
    }
}