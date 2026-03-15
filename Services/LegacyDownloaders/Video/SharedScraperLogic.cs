using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Siphon.Extensions;

namespace Siphon.Services.LegacyDownloaders.Video
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
                cleanName = cleanName.Replace(".mp4", "") + $"_{DateTime.Now.Ticks}.mp4";
            }

            return string.IsNullOrWhiteSpace(cleanName) ? "Video_Download" : cleanName;
        }

        public static async Task<string> ResolveRedGifsUrlAsync(string url, CancellationToken token)
        {
            var result = await ResolveRedGifsUrlWithDurationAsync(url, token);
            return result.Url;
        }

        public static async Task<(string Url, double Duration)> ResolveRedGifsUrlWithDurationAsync(string url, CancellationToken token)
        {
            if (!url.Contains("redgifs.com/watch/")) return (url, 0);

            var match = Regex.Match(url, @"watch/([a-zA-Z0-9]+)");
            if (!match.Success) return (url, 0);
            string id = match.Groups[1].Value.ToLower();

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            try
            {
                var authResp = await client.GetAsync("https://api.redgifs.com/v2/auth/temporary", token);
                if (!authResp.IsSuccessStatusCode) return (url, 0);

                var authJson = await authResp.Content.ReadAsStringAsync(token);
                var authObj = JsonNode.Parse(authJson);
                string accessToken = authObj?["token"]?.ToString();

                if (string.IsNullOrEmpty(accessToken)) return (url, 0);

                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                var gifResp = await client.GetAsync($"https://api.redgifs.com/v2/gifs/{id}", token);
                if (!gifResp.IsSuccessStatusCode) return (url, 0);

                var gifJson = await gifResp.Content.ReadAsStringAsync(token);
                var gifObj = JsonNode.Parse(gifJson);

                string mp4Url = gifObj?["gif"]?["urls"]?["hd"]?.ToString();

                double duration = 0;
                if (double.TryParse(gifObj?["gif"]?["duration"]?.ToString(), out double d))
                {
                    duration = d;
                }

                return (!string.IsNullOrEmpty(mp4Url) ? mp4Url : url, duration);
            }
            catch
            {
                return (url, 0); // Fallback to original url if API fails
            }
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

        public static async Task<string> ConvertToMp4Async(string inputPath, DownloadJob job, CancellationToken token, IWebHostEnvironment _env)
        {
            FileInfo originalFile = new FileInfo(inputPath);
            originalFile.Rename($"{Path.GetFileNameWithoutExtension(inputPath)}.{Path.GetExtension(inputPath)}.converting");

            string directory = Path.GetDirectoryName(inputPath);
            string fileNameNoExt = Path.GetFileNameWithoutExtension(inputPath);
            string finalOutputPath = Path.Combine(directory, $"{fileNameNoExt}.mp4");

            string convertOutputPath = Path.Combine(_env.WebRootPath, "Convert", $"{fileNameNoExt}.mp4");

            string editingPath = originalFile.FullName;

            if (string.Equals(Path.GetExtension(inputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                return inputPath;
            }

            job.Status = "Converting to MP4...";

            string args = $"-y -i \"{editingPath}\" -c:v libx264 -c:a aac -movflags +faststart \"{convertOutputPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync(token);

            if (process.ExitCode == 0)
            {
                try { if (File.Exists(editingPath)) File.Delete(editingPath); } catch { }
                try
                {
                    FileInfo outputFile = new FileInfo(convertOutputPath);
                    outputFile.Move(directory);
                    if (outputFile.FullName != finalOutputPath)
                    {
                        throw new Exception("Failed to move converted file to final location.");
                    }
                }
                catch { }
                return finalOutputPath;
            }
            else
            {
                throw new Exception($"FFmpeg conversion failed with code {process.ExitCode}");
            }
        }
    }
}