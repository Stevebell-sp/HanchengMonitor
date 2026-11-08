using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace GlassMonitor;

/// <summary>從 GitHub Releases 取得的最新版本資訊。</summary>
public sealed class UpdateInfo
{
    public string Version = "";
    public string Url = "";
    public string Sha256 = "";
    public string Notes = "";
}

/// <summary>
/// 自動更新：讀 GitHub 儲存庫的最新 Release，
/// 下載其中的安裝檔（*.exe），以 GitHub 提供的 SHA-256 摘要（或附檔 *.sha256）驗證後才執行。
/// </summary>
public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"HanchengMonitor-Updater/{Current.ToString(3)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => typeof(App).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>「owner/repo」格式檢查。</summary>
    public static bool IsValidRepo(string repo)
    {
        var parts = (repo ?? "").Trim().Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    }

    /// <summary>有新版本就回傳更新資訊，否則回傳 null。</summary>
    public static async Task<UpdateInfo?> CheckAsync(string repo)
    {
        repo = repo.Trim();
        if (!IsValidRepo(repo)) throw new InvalidOperationException("儲存庫格式應為「帳號/儲存庫名稱」");

        using var resp = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("找不到儲存庫或尚未發佈任何版本");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!System.Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            throw new InvalidDataException($"版本標籤格式不正確：{tag}");
        if (latest <= Current) return null;

        var info = new UpdateInfo
        {
            Version = latest.ToString(3),
            Notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
        };

        string? shaUrl = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && info.Url.Length == 0)
            {
                info.Url = url;
                // GitHub 會為每個附檔提供 "digest": "sha256:…"
                if (a.TryGetProperty("digest", out var d) && d.GetString() is string dg && dg.StartsWith("sha256:"))
                    info.Sha256 = dg["sha256:".Length..];
            }
            else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                shaUrl = url;
            }
        }
        if (info.Url.Length == 0) throw new InvalidDataException("最新版本裡沒有安裝檔");
        if (info.Sha256.Length == 0 && shaUrl != null)
            info.Sha256 = (await Http.GetStringAsync(shaUrl)).Trim().Split(' ', '\t', '\r', '\n')[0];
        return info;
    }

    /// <summary>下載安裝程式、比對 SHA-256，正確才執行。呼叫端接著要結束程式。</summary>
    public static async Task InstallAsync(UpdateInfo info)
    {
        if (!Uri.TryCreate(info.Url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("下載網址不是 https");
        if (string.IsNullOrWhiteSpace(info.Sha256))
            throw new InvalidDataException("找不到安裝檔的 SHA-256，基於安全不進行更新");

        byte[] data = await Http.GetByteArrayAsync(u);
        string hash = Convert.ToHexString(SHA256.HashData(data));
        if (!hash.Equals(info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下載的檔案驗證失敗（SHA-256 不符），已取消更新");

        string path = Path.Combine(Path.GetTempPath(), $"HanchengMonitor_Setup_{info.Version}.exe");
        await File.WriteAllBytesAsync(path, data);
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }
}
