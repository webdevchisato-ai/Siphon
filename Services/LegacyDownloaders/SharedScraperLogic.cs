using System.Text.RegularExpressions;
using System.Net;

namespace Siphon.Services.LegacyDownloaders
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

        public static string SanitizeFileName(string n)
        {
            string asciiOnly = Regex.Replace(n, @"[^\u0000-\u007F]+", "");
            string cleanName = Regex.Replace(asciiOnly, @"[^a-zA-Z0-9 _-]", "");
            cleanName = Regex.Replace(cleanName, @"\s+", " ").Trim();
            if (cleanName.Length > 220) cleanName = cleanName.Substring(0, 220);
            return string.IsNullOrWhiteSpace(cleanName) ? "Video_Download" : cleanName;
        }

        public static async Task DownloadWithProgressAsync(string url, string path, string refUrl, string name, int attempt, DownloadJob job)
        {
            // Configure HttpClient to use Tor Proxy
            var proxy = new WebProxy(PROXY_URL);
            var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };

            using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                client.DefaultRequestHeaders.Add("Referer", refUrl);

                using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {resp.StatusCode}");

                    var total = resp.Content.Headers.ContentLength ?? -1L;
                    bool unknown = total == -1;

                    using (var source = await resp.Content.ReadAsStreamAsync())
                    using (var dest = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        var buf = new byte[65536];
                        var read = 0;
                        var totalRead = 0L;
                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        while ((read = await source.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await dest.WriteAsync(buf, 0, read);
                            totalRead += read;

                            // Update Job Status periodically (every ~512KB)
                            if (totalRead % (1024 * 512) == 0)
                            {
                                double mb = totalRead / 1024.0 / 1024.0;
                                double sec = sw.Elapsed.TotalSeconds;
                                if (sec <= 0) sec = 0.001;
                                string spd = $"{((totalRead / sec) / 1024 / 1024):0.0} MB/s";
                                string pre = attempt > 1 ? $"[RETRY {attempt}] " : "";

                                if (unknown)
                                {
                                    job.Status = $"{pre}Downloading (Legacy)... {mb:0.0} MB @ {spd}";
                                    // Keep progress pulsing if unknown
                                    job.Progress = (job.Progress >= 90) ? 10 : job.Progress + 5;
                                }
                                else
                                {
                                    double pct = (double)totalRead / total * 100;
                                    job.Progress = pct;
                                    job.Status = $"{pre}Downloading (Legacy): {pct:0.0}% @ {spd}";
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}