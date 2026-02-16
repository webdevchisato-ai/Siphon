namespace Siphon.Extensions
{
    public static class FileExtensions
    {
        /// <summary>
        /// Renames a file to a new name, including the extension.
        /// Usage: myFileInfo.Rename("NewPhoto.png");
        /// </summary>
        public static FileInfo Rename(this FileInfo file, string newFullName)
        {
            if (file == null || !file.Exists)
            {
                throw new FileNotFoundException("Source file not found.", file?.FullName);
            }

            if (string.IsNullOrWhiteSpace(newFullName))
            {
                throw new ArgumentException("New file name cannot be empty.", nameof(newFullName));
            }

            string newPath = Path.Combine(file.DirectoryName ?? string.Empty, newFullName);

            file.MoveTo(newPath);
            return new FileInfo(newPath);
        }

        /// <summary>
        /// Moves a file to a target directory. Creates the directory and parents if they don't exist.
        /// Retains the original file name.
        /// Usage: myFileInfo.Move(@"C:\Logs\Archive");
        /// </summary>
        public static FileInfo Move(this FileInfo file, string targetDirectory)
        {
            if (file == null || !file.Exists)
            {
                throw new FileNotFoundException("Source file not found.", file?.FullName);
            }

            // Create the directory (and all parent directories) if they don't exist
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            string destPath = Path.Combine(targetDirectory, file.Name);

            // If a file with the same name already exists in the destination, 
            // File.Move / FileInfo.MoveTo will throw an exception. 
            // We'll let it throw to prevent accidental data loss, or you can add file.Delete() here.
            file.MoveTo(destPath);

            return new FileInfo(destPath);
        }
    }
}
