namespace Siphon.Models
{
    public class DownloadObject
    {
        public int Id { get; set; }
        public string URL { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public DateTime? DownloadDate { get; set; }
        public DateTime ArchiveDate { get; set; }
        public long? FileSize { get; set; }
        public bool IsArchived { get; set; }
    }
}
