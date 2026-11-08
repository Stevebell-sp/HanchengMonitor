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
            // 用 API 的附檔網址下載（帶 Accept: application/octet-stream），
            // 比 github.com/…/releases/download/… 穩定；後者曾整個版本回 504
            string url = a.GetProperty("url").GetString() ?? "";
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
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, shaUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            req.Headers.UserAgent.ParseAdd($"HanchengMonitor-Updater/{Current.ToString(3)}");
            using var r = await Download.SendAsync(req);
            r.EnsureSuccessStatusCode();
            info.Sha256 = (await r.Content.ReadAsStringAsync()).Trim().Split(' ', '\t', '\r', '\n')[0];
        }
        return info;
    }

    /// <summary>下載安裝程式、比對 SHA-256，正確才執行。呼叫端接著要結束程式。</summary>
    public static async Task InstallAsync(UpdateInfo info)
    {
        if (!Uri.TryCreate(info.Url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("下載網址不是 https");
        if (string.IsNullOrWhiteSpace(info.Sha256))
            throw new InvalidDataException("找不到安裝檔的 SHA-256，基於安全不進行更新");

        string path = Path.Combine(Path.GetTempPath(), $"HanchengMonitor_Setup_{info.Version}.exe");
        await DownloadWithRetryAsync(u, path);

        string hash;
        using (var fs = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
        if (!hash.Equals(info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidDataException("下載的檔案驗證失敗（SHA-256 不符），已取消更新");
        }
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }

    private static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>邊下載邊寫檔；遇到 GitHub 暫時性錯誤（5xx、逾時）最多重試 3 次。</summary>
    private static async Task DownloadWithRetryAsync(Uri url, string path)
    {
        int[] waitSeconds = { 3, 10, 30 };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/octet-stream");
                req.Headers.UserAgent.ParseAdd($"HanchengMonitor-Updater/{Current.ToString(3)}");
                using var resp = await Download.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if ((int)resp.StatusCode >= 500)
                    throw new HttpRequestException($"伺服器錯誤 {(int)resp.StatusCode}", null, resp.StatusCode);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(path);
                await src.CopyToAsync(dst);
                return;
            }
            catch (Exception ex) when (attempt < waitSeconds.Length && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds[attempt]));
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                throw new InvalidOperationException("GitHub 下載伺服器暫時沒有回應，請過幾分鐘再按一次「更新」。", ex);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException h => h.StatusCode == null || (int)h.StatusCode >= 500,
        TaskCanceledException => true,
        IOException => true,
        _ => false,
    };
}
