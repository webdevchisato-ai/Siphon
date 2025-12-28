using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Siphon.Services
{
    public class PreviewGenerator
    {
        private readonly ConcurrentDictionary<string, bool> _processingFiles = new();
        private readonly ILogger<PreviewGenerator> _logger;

        public PreviewGenerator(ILogger<PreviewGenerator> logger)
        {
            _logger = logger;
        }

        public bool IsProcessing(string filePath) => _processingFiles.ContainsKey(filePath);

        public void QueueGeneration(string videoPath)
        {
            if (_processingFiles.TryAdd(videoPath, true))
            {
                Task.Run(() => GenerateAssets(videoPath));
            }
        }

        private async Task GenerateAssets(string videoPath)
        {
            try
            {
                string thumbPath = Path.ChangeExtension(videoPath, ".jpg");
                string previewPath = videoPath.Replace(".mp4", "_preview.mp4");

                // 1. Generate Thumbnail
                if (!File.Exists(thumbPath))
                {
                    await RunFfmpeg($"-y -i \"{videoPath}\" -ss 00:00:10 -vframes 1 \"{thumbPath}\"");
                }

                // 2. Generate Montage
                if (!File.Exists(previewPath))
                {
                    double duration = await GetVideoDuration(videoPath);
                    if (duration > 10)
                    {
                        int t1 = (int)(duration * 0.1);
                        int t2 = (int)(duration * 0.4);
                        int t3 = (int)(duration * 0.7);

                        string filter = $"select='between(t,{t1},{t1 + 3})+between(t,{t2},{t2 + 3})+between(t,{t3},{t3 + 3})',setpts=N/FRAME_RATE/TB,scale=320:-2";
                        await RunFfmpeg($"-y -i \"{videoPath}\" -vf \"{filter}\" -an \"{previewPath}\"");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preview generation failed for {videoPath}: {ex.Message}");
            }
            finally
            {
                _processingFiles.TryRemove(videoPath, out _);
            }
        }

        private async Task RunFfmpeg(string args)
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false
            });
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