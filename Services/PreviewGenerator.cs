using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Siphon.Services
{
    public class PreviewGenerator
    {
        private readonly ConcurrentDictionary<string, bool> _processingFiles = new();
        private readonly ILogger<PreviewGenerator> _logger;

        // LIMIT: 5 Concurrent threads for generation
        private readonly SemaphoreSlim _semaphore = new(5);

        public PreviewGenerator(ILogger<PreviewGenerator> logger)
        {
            _logger = logger;
        }

        public bool IsProcessing(string filePath) => _processingFiles.ContainsKey(filePath);

        public void QueueGeneration(string videoPath)
        {
            if (_processingFiles.TryAdd(videoPath, true))
            {
                _logger.LogInformation($"[Preview] Queued: {Path.GetFileName(videoPath)}");

                // Fire and forget, but wrapped to handle semaphore waiting
                Task.Run(async () =>
                {
                    try
                    {
                        await _semaphore.WaitAsync();
                        await GenerateAssets(videoPath);
                    }
                    finally
                    {
                        _semaphore.Release();
                        _processingFiles.TryRemove(videoPath, out _);
                    }
                });
            }
        }

        private async Task GenerateAssets(string videoPath)
        {
            string fileName = Path.GetFileName(videoPath);
            _logger.LogInformation($"[Preview] Processing started: {fileName}");

            try
            {
                string thumbPath = Path.ChangeExtension(videoPath, ".jpg");
                string previewPath = videoPath.Replace(".mp4", "_preview.mp4");

                // 1. Generate Thumbnail
                // Optimization: Put -ss BEFORE -i for fast "Input Seeking" (instant jump)
                if (!File.Exists(thumbPath))
                {
                    await RunFfmpeg($"-y -ss 00:00:10 -i \"{videoPath}\" -frames:v 1 -q:v 5 \"{thumbPath}\"");
                }

                // 2. Generate Montage
                if (!File.Exists(previewPath))
                {
                    double duration = await GetVideoDuration(videoPath);
                    if (duration > 10)
                    {
                        int t1 = (int)(duration * 0.10); // 10% mark
                        int t2 = (int)(duration * 0.40); // 40% mark
                        int t3 = (int)(duration * 0.70); // 70% mark

                        // OPTIMIZATION: Multi-Input Seeking + Concat
                        // Instead of reading the whole file to find later frames, we open the file 3 times
                        // and jump instantly to the timestamps.

                        var sb = new StringBuilder();
                        sb.Append("-y ");

                        // Input 1
                        sb.Append($"-ss {t1} -t 3 -i \"{videoPath}\" ");
                        // Input 2
                        sb.Append($"-ss {t2} -t 3 -i \"{videoPath}\" ");
                        // Input 3
                        sb.Append($"-ss {t3} -t 3 -i \"{videoPath}\" ");

                        // Filter Complex: Concat 3 streams, Scale to 320p
                        // -preset ultrafast: sacrifices compression for maximum CPU speed
                        sb.Append("-filter_complex \"[0:v][1:v][2:v]concat=n=3:v=1:a=0,scale=320:-2[v]\" ");
                        sb.Append("-map \"[v]\" -c:v libx264 -preset ultrafast -crf 28 -an ");
                        sb.Append($"\"{previewPath}\"");

                        await RunFfmpeg(sb.ToString());
                    }
                }

                _logger.LogInformation($"[Preview] Completed: {fileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Preview] Failed for {fileName}: {ex.Message}");
            }
        }

        private async Task RunFfmpeg(string args)
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true // Optional: Redirect error if you want to debug ffmpeg output
            });

            // If you want to see ffmpeg errors in your logs, you can read StandardError here
            // But for now we just wait
            await p.WaitForExitAsync();
        }

        private async Task<double> GetVideoDuration(string videoPath)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                if (double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) return d;
            }
            catch { }
            return 0;
        }
    }
}