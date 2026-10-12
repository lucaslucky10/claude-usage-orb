using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageOrb;

internal enum PollStatus { Loading, Ok, RateLimited, AuthExpired, NoCredentials, NetworkError, ServerError }

/// <summary>
/// Polls the usage endpoint on the UI thread's synchronization context. The last good snapshot is kept
/// (and cached on disk) so the orb never goes blank.
///
/// The endpoint allows only a few requests per account every few minutes (shared by every PC and tool
/// signed in to the account), answers 429 with "Retry-After: 0", and rejected requests still count.
/// So instead of exponential backoff the poller paces itself: it slows down a little on each 429
/// (never beyond 4 minutes) and speeds back up to the chosen interval after a run of successes.
/// Manual refreshes are honoured but never sent closer than 45 s apart.
/// </summary>
internal sealed class UsagePoller : IDisposable
{
    private static readonly TimeSpan MinRequestGap = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaxPace = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RenewGap = TimeSpan.FromMinutes(15);

    private readonly Settings settings;
    private readonly UsageApi api = new();
    private CancellationTokenSource? waitCts;
    private bool running;
    private int rateLimitStrikes;
    private int networkStrikes;
    private int serverStrikes;
    private DateTimeOffset lastRenew = DateTimeOffset.MinValue;
    private TimeSpan pace;
    private int okStreak;
    private readonly Random jitter = new();
    private string? lastBody;

    public UsagePoller(Settings settings)
    {
        this.settings = settings;
        pace = TimeSpan.FromSeconds(settings.PollSeconds);
    }

    public event Action? Changed;

    public UsageSnapshot? Snapshot { get; private set; }
    public PollStatus Status { get; private set; } = PollStatus.Loading;
    public int? LastHttpStatus { get; private set; }
    public DateTimeOffset? LastAttempt { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }
    public DateTimeOffset? NextAttempt { get; private set; }
    public string? Plan { get; private set; }
    public bool IsFetching { get; private set; }
    public bool IsRenewing { get; private set; }

    /// <summary>Set while a manual refresh is waiting for the minimum gap between requests.</summary>
    public DateTimeOffset? ManualRefreshAt { get; private set; }

    /// <summary>True when the shown numbers are older than a normal refresh cycle would allow.</summary>
    public bool IsStale(DateTimeOffset now)
    {
        if (Snapshot is null)
        {
            return true;
        }
        var maxAge = TimeSpan.FromSeconds(Math.Max(Math.Max(settings.PollSeconds, pace.TotalSeconds) * 2.5, 360));
        return now - Snapshot.FetchedAt > maxAge;
    }

    public void Start()
    {
        LoadCache();
        running = true;
        _ = LoopAsync();
    }

    public void RefreshNow()
    {
        if (IsFetching)
        {
            return;
        }
        var now = DateTimeOffset.Now;
        DateTimeOffset earliest = LastAttempt is { } last ? last + MinRequestGap : now;
        ManualRefreshAt = earliest > now ? earliest : now;
        waitCts?.Cancel();
        Changed?.Invoke();
    }

    /// <summary>The chosen interval changed in the menu.</summary>
    public void IntervalChanged()
    {
        pace = TimeSpan.FromSeconds(settings.PollSeconds);
        okStreak = 0;
        RefreshNow();
    }

    private async Task LoopAsync()
    {
        while (running)
        {
            TimeSpan delay;
            try
            {
                delay = await PollOnceAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Poll failed unexpectedly", ex);
                delay = TimeSpan.FromSeconds(60);
            }

            DateTimeOffset due = DateTimeOffset.Now + delay;
            while (running)
            {
                NextAttempt = due;
                Changed?.Invoke();
                TimeSpan wait = due - DateTimeOffset.Now;
                if (wait <= TimeSpan.Zero)
                {
                    break;
                }
                using var cts = new CancellationTokenSource();
                waitCts = cts;
                try
                {
                    await Task.Delay(wait, cts.Token);
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Manual refresh: go at the requested time (already spaced from the last request).
                    if (ManualRefreshAt is { } manual)
                    {
                        due = manual;
                    }
                }
                finally
                {
                    waitCts = null;
                }
            }
            ManualRefreshAt = null;
        }
    }

    private async Task<TimeSpan> PollOnceAsync()
    {
        var creds = await Task.Run(() => CredentialStore.TryRead(settings.ClaudeConfigDir));
        if (creds is null)
        {
            SetStatus(PollStatus.NoCredentials);
            return TimeSpan.FromSeconds(60);
        }
        Plan = creds.SubscriptionType;

        if (creds.IsExpired(DateTimeOffset.Now))
        {
            creds = await TryRecoverTokenAsync(creds);
            if (creds.IsExpired(DateTimeOffset.Now))
            {
                SetStatus(PollStatus.AuthExpired);
                // Re-reading a local file is free, so check often for Claude Code's renewed token.
                return TimeSpan.FromSeconds(30);
            }
        }

        IsFetching = true;
        Changed?.Invoke();
        LastAttempt = DateTimeOffset.Now;

        ApiResult result;
        try
        {
            result = await api.FetchAsync(creds.AccessToken, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            networkStrikes++;
            Log.Warn($"Network error #{networkStrikes}: {ex.GetType().Name}: {ex.Message}");
            SetStatus(PollStatus.NetworkError);
            return Backoff(networkStrikes, 15, 30, 60, 120);
        }
        finally
        {
            IsFetching = false;
        }

        LastHttpStatus = result.Status;
        switch (result.Status)
        {
            case 200:
                return HandleSuccess(result.Body);

            case 401:
            case 403:
            {
                Log.Warn($"Usage endpoint returned {result.Status}.");
                var reread = CredentialStore.TryRead(settings.ClaudeConfigDir);
                if (reread is not null && reread.AccessToken != creds.AccessToken && !reread.IsExpired(DateTimeOffset.Now))
                {
                    return TimeSpan.FromSeconds(2);
                }
                await TryRecoverTokenAsync(creds);
                SetStatus(PollStatus.AuthExpired);
                return TimeSpan.FromSeconds(60);
            }

            case 429:
            {
                rateLimitStrikes++;
                okStreak = 0;
                // Rejected requests count too, so slow down gently instead of hammering or giving up for minutes.
                pace = TimeSpan.FromSeconds(Math.Min(MaxPace.TotalSeconds, Math.Max(90, pace.TotalSeconds * 1.25)));
                TimeSpan wait = pace + TimeSpan.FromSeconds(jitter.Next(0, 15));
                if (result.RetryAfter is { } ra && ra > wait)
                {
                    wait = ra < MaxPace ? ra : MaxPace;
                }
                Log.Warn($"Rate limited (#{rateLimitStrikes}), next try in {wait.TotalSeconds:0}s (pace {pace.TotalSeconds:0}s).");
                SetStatus(PollStatus.RateLimited);
                return wait;
            }

            default:
                serverStrikes++;
                Log.Warn($"Usage endpoint returned {result.Status}.");
                SetStatus(PollStatus.ServerError);
                return Backoff(serverStrikes, 60, 120, 300, 600);
        }
    }

    private TimeSpan HandleSuccess(string body)
    {
        var now = DateTimeOffset.Now;
        UsageSnapshot snapshot;
        try
        {
            snapshot = UsageParser.Parse(body, now);
        }
        catch (Exception ex)
        {
            serverStrikes++;
            Log.Error("Could not parse usage response", ex);
            SetStatus(PollStatus.ServerError);
            return Backoff(serverStrikes, 120, 300, 600);
        }

        Snapshot = snapshot;
        lastBody = body;
        LastSuccess = now;
        rateLimitStrikes = networkStrikes = serverStrikes = 0;
        TimeSpan chosen = TimeSpan.FromSeconds(settings.PollSeconds);
        if (pace > chosen && ++okStreak >= 3)
        {
            pace = pace - TimeSpan.FromSeconds(20) > chosen ? pace - TimeSpan.FromSeconds(20) : chosen;
            okStreak = 0;
        }
        SetStatus(PollStatus.Ok);
        SaveCache(body, now);

        // Poll on schedule, but also right after a window resets so the orb never shows a stale "almost empty".
        TimeSpan delay = pace;
        foreach (var limit in snapshot.Limits)
        {
            if (limit.ResetsAt is { } reset && reset > now)
            {
                TimeSpan untilReset = reset - now + TimeSpan.FromSeconds(20);
                if (untilReset < delay)
                {
                    delay = untilReset < MinRequestGap ? MinRequestGap : untilReset;
                }
            }
        }
        return delay;
    }

    private async Task<ClaudeCredentials> TryRecoverTokenAsync(ClaudeCredentials current)
    {
        ClaudeCredentials latest = CredentialStore.TryRead(settings.ClaudeConfigDir) ?? current;
        var now = DateTimeOffset.Now;
        if (!latest.IsExpired(now) && latest.AccessToken != current.AccessToken)
        {
            return latest;
        }
        if (!settings.AutoRenewToken || now - lastRenew < RenewGap)
        {
            return latest;
        }
        lastRenew = now;
        SetStatus(PollStatus.AuthExpired);
        IsRenewing = true;
        Changed?.Invoke();
        try
        {
            await ClaudeCli.RenewTokenAsync(settings.ClaudeConfigDir);
        }
        finally
        {
            IsRenewing = false;
        }
        return CredentialStore.TryRead(settings.ClaudeConfigDir) ?? latest;
    }

    /// <summary>Lets the user retry renewal right away (e.g. after turning the option on).</summary>
    public void AllowRenewNow()
    {
        lastRenew = DateTimeOffset.MinValue;
        waitCts?.Cancel();
    }

    private void SetStatus(PollStatus status)
    {
        if (Status != status)
        {
            Log.Info($"Status {Status} -> {status}");
        }
        Status = status;
        Changed?.Invoke();
    }

    private static TimeSpan Backoff(int strikes, params int[] seconds) =>
        TimeSpan.FromSeconds(seconds[Math.Clamp(strikes - 1, 0, seconds.Length - 1)]);

    private sealed record CacheEntry(DateTimeOffset FetchedAt, string Body);

    private static void SaveCache(string body, DateTimeOffset fetchedAt)
    {
        try
        {
            File.WriteAllText(AppPaths.CacheFile, JsonSerializer.Serialize(new CacheEntry(fetchedAt, body)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write cache: {ex.Message}");
        }
    }

    /// <summary>Rebuilds labels from the last response, e.g. after switching the UI language.</summary>
    public void Reparse()
    {
        if (lastBody is not null && Snapshot is not null)
        {
            Snapshot = UsageParser.Parse(lastBody, Snapshot.FetchedAt);
            Changed?.Invoke();
        }
    }

    /// <summary>Loads the on-disk snapshot without starting the poll loop (preview mode).</summary>
    public void LoadCachedSnapshot()
    {
        LoadCache();
        if (Snapshot is not null)
        {
            Status = PollStatus.Ok;
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.CacheFile))
            {
                return;
            }
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(AppPaths.CacheFile));
            if (entry is not null && DateTimeOffset.Now - entry.FetchedAt < TimeSpan.FromDays(8))
            {
                Snapshot = UsageParser.Parse(entry.Body, entry.FetchedAt);
                lastBody = entry.Body;
                LastSuccess = entry.FetchedAt;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring unreadable cache: {ex.Message}");
        }
    }

    public void Dispose()
    {
        running = false;
        waitCts?.Cancel();
        api.Dispose();
    }
}
