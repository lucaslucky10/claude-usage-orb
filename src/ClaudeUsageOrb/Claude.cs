using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageOrb;

internal sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } exp && exp <= now.AddSeconds(30);
}

/// <summary>Reads the OAuth login that Claude Code keeps on disk. The token is only ever sent to api.anthropic.com.</summary>
internal static class CredentialStore
{
    public static string ConfigDir(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return Environment.ExpandEnvironmentVariables(overrideDir);
        }
        string? env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    public static ClaudeCredentials? TryRead(string? overrideDir)
    {
        string path = Path.Combine(ConfigDir(overrideDir), ".credentials.json");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                string? token = oauth.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (string.IsNullOrEmpty(token))
                {
                    return null;
                }
                DateTimeOffset? expires = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64())
                    : null;
                string? plan = oauth.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                return new ClaudeCredentials(token, expires, plan);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Claude Code may be rewriting the file right now.
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                Log.Warn($"Reading credentials failed: {ex.GetType().Name}");
                return null;
            }
        }
        return null;
    }
}

internal sealed record ApiResult(int Status, string Body, TimeSpan? RetryAfter);

internal sealed class UsageApi : IDisposable
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public async Task<ApiResult> FetchAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"claude-usage-orb/{typeof(UsageApi).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter is { } ra)
        {
            retryAfter = ra.Delta ?? (ra.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        }
        return new ApiResult((int)response.StatusCode, body, retryAfter);
    }

    public void Dispose() => http.Dispose();
}

/// <summary>
/// Claude Code renews its own OAuth token whenever it runs. When the stored token has expired we start
/// a harmless, zero-usage CLI command so Claude Code gets a chance to renew it. We never touch the
/// refresh token ourselves.
/// </summary>
internal static class ClaudeCli
{
    public static string? Find()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new System.Collections.Generic.List<string>
        {
            Path.Combine(home, ".local", "bin"),
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(appData, "npm"));

        foreach (string ext in new[] { ".exe", ".cmd" })
        {
            foreach (string dir in dirs)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim().Trim('"'), "claude" + ext);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry.
                }
            }
        }
        return null;
    }

    public static async Task NudgeTokenRefreshAsync(string? configDirOverride)
    {
        string? exe = Find();
        if (exe is null)
        {
            Log.Warn("Token expired and claude CLI was not found.");
            return;
        }

        var psi = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo(exe)
            : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { ArgumentList = { "/d", "/c", exe } };
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--json");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(configDirOverride))
        {
            psi.Environment["CLAUDE_CONFIG_DIR"] = Environment.ExpandEnvironmentVariables(configDirOverride);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return;
            }
            // Output may contain account details: read it only to drain the pipes, never log it.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            await Task.WhenAll(stdout, stderr);
            Log.Info($"Ran claude auth status to nudge token refresh (exit {(process.HasExited ? process.ExitCode : -1)}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not run claude CLI: {ex.Message}");
        }
    }
}
