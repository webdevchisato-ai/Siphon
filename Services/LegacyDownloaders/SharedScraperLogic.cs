using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace Siphon.Services
{
    public static class SharedScraperLogic
    {
        // Use the container's Tor proxy
        private const string PROXY_URL = "socks5://127.0.0.1:9050";

        public static string CleanTitle(string r)
        {
            r = Regex.Replace(r, @"\s*\d+min.*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(r, @"\s*\d+p\d+fps.*$", "", RegexOptions.IgnoreCase);
        }

        public static string SanitizeFileName(string n, string downloadPath)
        {
            string asciiOnly = Regex.Replace(n, @"[^\u0000-\u007F]+", "");
            string cleanName = Regex.Replace(asciiOnly, @"[^a-zA-Z0-9 _-]", "");
            cleanName = Regex.Replace(cleanName, @"\s+", " ").Trim();
            if (cleanName.Length > 220) cleanName = cleanName.Substring(0, 220);

            if (File.Exists(Path.Combine(downloadPath, $"{n}.mp4")))
            {
                cleanName += $"_{DateTime.Now.Ticks}";
            }

            return string.IsNullOrWhiteSpace(cleanName) ? "Video_Download" : cleanName;
        }

        public static async Task DownloadWithProgressAsync(string url, string path, string refUrl, string name, int attempt, DownloadJob job, CancellationToken token)
        {
            // 1. Define the temporary .part path
            string tempPath = path + ".part";

            if (File.Exists(tempPath))
            {
                tempPath = $"{path}_{DateTime.Now.Ticks}.part";
            }

            // Configure HttpClient to use Tor Proxy
            var proxy = new WebProxy(PROXY_URL);
            var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };

            using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                client.DefaultRequestHeaders.Add("Referer", refUrl);

                // Pass token to GetAsync
                using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {resp.StatusCode}");

                    var total = resp.Content.Headers.ContentLength ?? -1L;
                    bool unknown = total == -1;

                    // Pass token to ReadAsStreamAsync
                    using (var source = await resp.Content.ReadAsStreamAsync(token))
                    using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        var buf = new byte[65536];
                        var read = 0;
                        var totalRead = 0L;
                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        // Pass token to ReadAsync
                        while ((read = await source.ReadAsync(buf, 0, buf.Length, token)) > 0)
                        {
                            // Pass token to WriteAsync
                            await dest.WriteAsync(buf, 0, read, token);
                            totalRead += read;

                            // Update Job Status periodically (every ~512KB)
                            if (totalRead % (1024 * 512) == 0)
                            {
                                double mb = totalRead / 1024.0 / 1024.0;
                                double sec = sw.Elapsed.TotalSeconds;
                                if (sec <= 0) sec = 0.001;
                                string spd = $"{((totalRead / sec) / 1024 / 1024):0.0} MB/s";
                                string pre = attempt > 1 ? $"[RETRY {attempt}] " : "";

                                job.DownloadSpeed = spd; // Update UI property

                                if (unknown)
                                {
                                    job.Status = $"{pre}Downloading (Legacy)... {mb:0.0} MB";
                                    // Keep progress pulsing if unknown
                                    job.Progress = (job.Progress >= 90) ? 10 : job.Progress + 5;
                                }
                                else
                                {
                                    double pct = (double)totalRead / total * 100;
                                    job.Progress = pct;
                                    job.Status = $"{pre}Downloading";
                                }
                            }
                        }
                    }
                }
            }

            // 2. Rename .part to final filename
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
            File.Move(tempPath, path);
        }

        public static async Task<string> ConvertToMp4Async(string inputPath, DownloadJob job, CancellationToken token)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string fileNameNoExt = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(directory, $"{fileNameNoExt}.mp4");

            // Safety check: Don't convert if it's already mp4 (should be handled by caller, but safe to check)
            if (string.Equals(Path.GetExtension(inputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                return inputPath;
            }

            job.Status = "Converting to MP4...";

            // Command: 
            // -y: Overwrite output
            // -i: Input file
            // -c:v libx264: Use H.264 video codec
            // -c:a aac: Use AAC audio codec
            // -movflags +faststart: Move metadata to start of file (good for web playback)
            string args = $"-y -i \"{inputPath}\" -c:v libx264 -c:a aac -movflags +faststart \"{outputPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true, // FFmpeg writes stats to stderr
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // We can wait for exit. 
            // Note: Parsing FFmpeg progress from stderr is possible but complex. 
            // For now, an indeterminate status is usually fine for conversion.
            await process.WaitForExitAsync(token);

            if (process.ExitCode == 0)
            {
                // Conversion success: Delete the original non-mp4 file
                try { if (File.Exists(inputPath)) File.Delete(inputPath); } catch { }
                return outputPath;
            }
            else
            {
                throw new Exception($"FFmpeg conversion failed with code {process.ExitCode}");
            }
        }
    }
}