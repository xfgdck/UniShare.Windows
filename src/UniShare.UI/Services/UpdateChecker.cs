using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace UniShare.UI.Services
{
    public record ReleaseInfo(string Version, string PageUrl);

    /// <summary>
    /// 手动检查更新：查询 GitHub 最新发布版本（程序不会自动检查）
    /// </summary>
    public static class UpdateChecker
    {
        public const string RepositoryUrl = "https://github.com/xfgdck/UniShare.Windows";
        public const string ReleasesUrl = RepositoryUrl + "/releases";

        private const string LatestReleaseApi = "https://api.github.com/repos/xfgdck/UniShare.Windows/releases/latest";

        public static async Task<ReleaseInfo> GetLatestReleaseAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UniShare", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            string json = await client.GetStringAsync(LatestReleaseApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            string pageUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? ReleasesUrl : ReleasesUrl;
            return new ReleaseInfo(tag.TrimStart('v', 'V'), pageUrl);
        }

        /// <summary>
        /// 比较两个日期版本号（如 2026.10.27），无法解析时按字符串比较
        /// </summary>
        public static bool IsNewer(string latest, string current)
        {
            if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
            {
                return l > c;
            }
            return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
        }
    }
}
