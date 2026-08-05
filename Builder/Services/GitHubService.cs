using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Builder.Services;

public class GitHubOwner
{
    [JsonPropertyName("login")] public string Login { get; set; } = string.Empty;
}

public class GitHubRepository
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("full_name")] public string FullName { get; set; } = string.Empty;
    [JsonPropertyName("clone_url")] public string CloneUrl { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("private")] public bool IsPrivate { get; set; }
    [JsonPropertyName("fork")] public bool IsFork { get; set; }
    [JsonPropertyName("archived")] public bool IsArchived { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("owner")] public GitHubOwner? Owner { get; set; }
}

/// <summary>
/// GitHub REST API からユーザー/組織のリポジトリ一覧を取得します。
/// 認証トークンは環境変数 (GH_TOKEN / GITHUB_TOKEN) か gh CLI (gh auth token) から取得し、
/// 取得できた場合はプライベートリポジトリも一覧に含めます。
/// </summary>
public class GitHubService
{
    private const string ApiBase = "https://api.github.com";
    private const int PerPage = 100;
    private const int MaxPages = 10;

    // GitHub のユーザー名/組織名は英数字とハイフンのみ (先頭・末尾はハイフン不可、39文字以内)
    private static readonly Regex OwnerPattern =
        new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$", RegexOptions.Compiled);

    // github.com/<owner> と同じ形だが所有者ではないパス
    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "explore", "features", "login", "logout", "marketplace",
        "notifications", "pricing", "pulls", "search", "settings", "sponsors", "topics", "trending"
    };

    private static readonly HttpClient Http = CreateHttpClient();
    private static string? _cachedToken;
    private static bool _tokenResolved;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Builder");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>
    /// 入力がリポジトリ単体ではなくユーザー/組織を指しているかを判定します。
    /// 例: "https://github.com/sk0ya/" "github.com/sk0ya" "sk0ya" → sk0ya
    /// </summary>
    public static bool TryParseOwner(string input, out string owner)
    {
        owner = string.Empty;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        // git@github.com:user/repo.git 形式はリポジトリ指定なので対象外
        if (text.StartsWith("git@", StringComparison.OrdinalIgnoreCase)) return false;

        if (text.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!text.Contains("://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
                return false;

            var segments = uri.AbsolutePath.Trim('/')
                              .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            // https://github.com/orgs/<org>/repositories 形式にも対応
            if (segments[0].Equals("orgs", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length < 2) return false;
                return Accept(segments[1], out owner);
            }

            // <owner> のみ (リポジトリ名が付いていたら単体クローン扱い)
            if (segments.Length != 1) return false;
            return Accept(segments[0], out owner);
        }

        // URL ではなくユーザー名だけの入力
        if (text.Contains('/') || text.Contains(':')) return false;
        return Accept(text, out owner);

        static bool Accept(string candidate, out string owner)
        {
            owner = string.Empty;
            if (ReservedPaths.Contains(candidate)) return false;
            if (!OwnerPattern.IsMatch(candidate)) return false;
            owner = candidate;
            return true;
        }
    }

    /// <summary>
    /// 指定した所有者のリポジトリ一覧を取得します。トークンがある場合はプライベート分もマージします。
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepository>> GetRepositoriesAsync(
        string owner, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);
        var repos = new Dictionary<string, GitHubRepository>(StringComparer.OrdinalIgnoreCase);
        Exception? publicError = null;

        try
        {
            await FetchPagesAsync($"{ApiBase}/users/{Uri.EscapeDataString(owner)}/repos" +
                                  $"?per_page={PerPage}&sort=updated&type=owner",
                                  token, owner, repos, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // トークン経由でプライベートリポジトリが取れる可能性があるため、ここでは中断しない
            publicError = ex;
        }

        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                await FetchPagesAsync($"{ApiBase}/user/repos?per_page={PerPage}&sort=updated" +
                                      "&affiliation=owner,collaborator,organization_member",
                                      token, owner, repos, ct);
            }
            catch (Exception) when (publicError == null && repos.Count > 0)
            {
                // 公開分が取れているならプライベート分の失敗は無視する
            }
        }

        if (repos.Count == 0 && publicError != null) throw publicError;

        return repos.Values
                    .OrderByDescending(r => r.UpdatedAt)
                    .ToList();
    }

    private async Task FetchPagesAsync(
        string url, string? token, string owner,
        Dictionary<string, GitHubRepository> sink, CancellationToken ct)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}&page={page}");
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeError(response, owner));

            var json = await response.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<GitHubRepository>>(json, JsonOptions) ?? [];

            foreach (var repo in items)
            {
                // /user/repos は他所有者のリポジトリも返すため所有者で絞り込む
                if (!string.Equals(repo.Owner?.Login, owner, StringComparison.OrdinalIgnoreCase)) continue;
                sink.TryAdd(repo.FullName, repo);
            }

            if (items.Count < PerPage) return;
        }
    }

    private static string DescribeError(HttpResponseMessage response, string owner) => response.StatusCode switch
    {
        HttpStatusCode.NotFound =>
            $"ユーザー/組織「{owner}」が見つかりませんでした。",
        HttpStatusCode.Unauthorized =>
            "GitHubの認証に失敗しました。トークンを確認してください。",
        HttpStatusCode.Forbidden when response.Headers.TryGetValues("x-ratelimit-remaining", out var v) && v.FirstOrDefault() == "0" =>
            "GitHub APIのレート制限に達しました。しばらく待つか、gh CLIでログイン (gh auth login) してください。",
        HttpStatusCode.Forbidden =>
            "GitHub APIへのアクセスが拒否されました。",
        _ => $"GitHub APIの呼び出しに失敗しました ({(int)response.StatusCode} {response.ReasonPhrase})。"
    };

    /// <summary>環境変数、なければ gh CLI からアクセストークンを取得します（1プロセス1回だけ解決）。</summary>
    private static async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_tokenResolved) return _cachedToken;

        var fromEnv = Environment.GetEnvironmentVariable("GH_TOKEN")
                      ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _cachedToken = !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv.Trim() : await TryGetGhCliTokenAsync(ct);
        _tokenResolved = true;
        return _cachedToken;
    }

    private static async Task<string?> TryGetGhCliTokenAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0) return null;
            var token = output.Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            // gh CLI が無い / 未ログインの場合は匿名アクセスにフォールバック
            return null;
        }
    }
}
