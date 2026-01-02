using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Siphon.Services
{
    public class PreviewGenerator
    {
        private readonly ConcurrentDictionary<string, bool> _processingFiles = new();
        private readonly ILogger<PreviewGenerator> _logger;
        private readonly UserService _userService;

        // LIMIT: 5 Concurrent threads for generation
        private readonly SemaphoreSlim _semaphore = new(5);

        public PreviewGenerator(ILogger<PreviewGenerator> logger, UserService userService)
        {
            _logger = logger;
            _userService = userService;
        }

        public bool IsProcessing(string filePath) => _processingFiles.ContainsKey(filePath);

        public void QueueGeneration(string videoPath)
        {
            if (_processingFiles.TryAdd(videoPath, true))
            {
                _logger.LogInformation($"[Preview] Queued: {Path.GetFileName(videoPath)}");

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
                string thumbPath = videoPath.Replace(".mp4", "_preview.jpg");
                string previewPath = videoPath.Replace(".mp4", "_preview.mp4");

                // 1. Generate Thumbnail
                if (!File.Exists(thumbPath))
                {
                    // Use -ss 00:00:01 for very short videos to ensure we get a frame
                    // If video is < 1s, ffmpeg usually defaults to the first frame anyway
                    await RunFfmpeg($"-y -ss 00:00:01 -i \"{videoPath}\" -frames:v 1 -q:v 5 \"{thumbPath}\"");
                }

                // 2. Generate Preview Video
                if (!File.Exists(previewPath))
                {
                    double duration = await GetVideoDuration(videoPath);

                    if (duration > 10)
                    {
                        // --- LONG VIDEO (>10s): Create 3-part Montage ---
                        int t1 = (int)(duration * 0.10);
                        int t2 = (int)(duration * 0.40);
                        int t3 = (int)(duration * 0.70);

                        var sb = new StringBuilder();
                        sb.Append("-y ");
                        sb.Append($"-ss {t1} -t 3 -i \"{videoPath}\" ");
                        sb.Append($"-ss {t2} -t 3 -i \"{videoPath}\" ");
                        sb.Append($"-ss {t3} -t 3 -i \"{videoPath}\" ");

                        sb.Append("-filter_complex \"[0:v][1:v][2:v]concat=n=3:v=1:a=0,scale=320:-2[v]\" ");
                        sb.Append("-map \"[v]\" -c:v libx264 -preset ultrafast -crf 28 -an ");
                        sb.Append($"\"{previewPath}\"");

                        await RunFfmpeg(sb.ToString());
                    }
                    else if (duration > 0)
                    {
                        // --- SHORT VIDEO (<=10s): Convert the whole thing ---
                        // Just scale it down and remove audio. No seeking needed.
                        var sb = new StringBuilder();
                        sb.Append($"-y -i \"{videoPath}\" ");
                        sb.Append("-vf \"scale=320:-2\" ");
                        sb.Append("-c:v libx264 -preset ultrafast -crf 28 -an ");
                        sb.Append($"\"{previewPath}\"");

                        await RunFfmpeg(sb.ToString());
                    }
                }

                if (_userService.GetGenerateHeatmapStatus())
                {
                    string mapPath = Path.ChangeExtension(videoPath, ".json");
                    if (!File.Exists(mapPath))
                    {
                        _logger.LogInformation($"[Preview] Generating volume map for: {fileName}");
                        await GenerateVolumeMap(videoPath, mapPath);
                    }
                }

                _logger.LogInformation($"[Preview] Completed: {fileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Preview] Failed for {fileName}: {ex.Message}");
            }
        }

        private async Task GenerateVolumeMap(string videoPath, string jsonOutputPath)
        {
            try
            {
                // 1. Get Duration first to determine sampling size
                double duration = await GetVideoDuration(videoPath);
                if (duration <= 0) duration = 1;

                // We want exactly 100 data points for the graph to fit perfectly in CSS (1 bar = 1% width)
                int desiredPoints = 50;

                //sensitivity multiplier for visual boost
                double sensitivity = 2.7;

                // Calculate sample rate to get roughly the right amount of data in one pass
                // We'll dump raw PCM data. 
                // 100 points over 'duration' seconds. 
                // Let's sample at 1000Hz to have enough data to smooth out.
                int sampleRate = 4000;

                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    // -map 0:a selects audio stream
                    // -ac 1 mixes to mono
                    // -ar sets sample rate
                    // -f s16le outputs raw 16-bit PCM samples
                    Arguments = $"-i \"{videoPath}\" -map 0:a -ac 1 -ar {sampleRate} -f s16le -",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true // hide ffmpeg logs
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                // Read the raw stream
                using var ms = new MemoryStream();
                await process.StandardOutput.BaseStream.CopyToAsync(ms);
                await process.WaitForExitAsync();

                byte[] rawData = ms.ToArray();

                // Calculate RMS (Root Mean Square) for chunks
                List<int> volumePoints = new List<int>();

                // 16-bit audio = 2 bytes per sample
                int bytesPerSample = 2;
                int totalSamples = rawData.Length / bytesPerSample;

                // How many samples to average into one "Bar" of the graph
                int samplesPerChunk = totalSamples / desiredPoints;
                if (samplesPerChunk < 1) samplesPerChunk = 1;

                for (int i = 0; i < totalSamples; i += samplesPerChunk)
                {
                    double sumSquares = 0;
                    int count = 0;

                    for (int j = 0; j < samplesPerChunk && (i + j) * 2 < rawData.Length; j++)
                    {
                        // Read Int16 from bytes
                        short sample = BitConverter.ToInt16(rawData, (i + j) * 2);
                        // Normalize to 0-1 range (Short.MaxValue is 32767)
                        double norm = sample / 32768.0;
                        sumSquares += norm * norm;
                        count++;
                    }

                    if (count > 0)
                    {
                        double rms = Math.Sqrt(sumSquares / count);
                        // Boost the signal a bit visually so quiet isn't invisible
                        // Convert to 0-100 integer
                        double boosted = Math.Pow(rms, 0.5);
                        int val = (int)(Math.Min(1.0, boosted * sensitivity) * 100);
                        volumePoints.Add(val);
                    }
                }
                _logger.LogInformation($"[VolumeMap] Generated {volumePoints.Count} points for: {Path.GetFileName(videoPath)}");
                // Save to JSON
                await File.WriteAllTextAsync(jsonOutputPath, JsonSerializer.Serialize(volumePoints));
            }
            catch (Exception ex)
            {
                _logger.LogError($"[VolumeMap] Failed: {ex.Message}");
                // Create an empty array so UI doesn't crash
                await File.WriteAllTextAsync(jsonOutputPath, "[]");
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
                RedirectStandardError = true
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