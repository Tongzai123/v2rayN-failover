using ServiceLib.Services.FailoverRelay;

namespace ServiceLib.Manager;

public sealed class FailoverRelayManager
{
    private static readonly Lazy<FailoverRelayManager> _instance = new(() => new FailoverRelayManager());
    public static FailoverRelayManager Instance => _instance.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _statusWriteGate = new(1, 1);
    private readonly object _failureCountLock = new();
    private readonly Dictionary<string, int> _dailyFailureCounts = new();
    private readonly Dictionary<string, ExternalStatusWriteSnapshot> _lastExternalStatusWrites = new();
    private DateOnly _failureCountDate = DateOnly.FromDateTime(DateTime.Now);
    private FailoverRelayService? _service;

    public int CurrentListenPort { get; private set; }
    public string? CurrentActiveProfileId { get; private set; }

    public async Task<RetResult> StartAsync(FailoverRelayRuntime runtime)
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            if (!runtime.Enabled)
            {
                return new RetResult(false, ResUI.FailedGenDefaultConfiguration);
            }

            CurrentActiveProfileId = null;
            var firstByteTimeoutMs = FailoverRelayOptions.NormalizeFirstByteTimeoutMs(
                AppManager.Instance.Config?.GuiItem?.FailoverRelayFirstByteTimeoutMs ?? 0);
            _service = new FailoverRelayService(new FailoverRelayOptions
            {
                ListenPort = runtime.ListenPort,
                CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(firstByteTimeoutMs),
                CandidateSecondRoundFirstByteTimeout = TimeSpan.FromMilliseconds(firstByteTimeoutMs + FailoverRelayOptions.SecondRoundFirstByteTimeoutExtraMs),
                SpeedPingTestUrl = GetRelayHealthProbeUrl(),
                HealthConfirmationProbeUrls = FailoverRelayOptions.DefaultHealthConfirmationProbeUrls,
                CandidateFailureReporterAsync = ReportCandidateFailureAsync,
                CandidateSelectedReporterAsync = ReportCandidateSelectedAsync,
                CandidateStatusReporterAsync = ReportCandidateStatusAsync,
            }, runtime.Candidates);
            await _service.StartAsync(CancellationToken.None);
            CurrentListenPort = _service.ListenPort;
            return new RetResult(true, string.Empty);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(FailoverRelayManager), ex);
            await StopCoreAsync();
            return new RetResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void MarkHealthRecovered(string profileId)
    {
        _service?.MarkHealthRecovered(profileId);
    }

    private static string GetRelayHealthProbeUrl()
    {
        var configured = AppManager.Instance.Config?.SpeedTestItem?.SpeedPingTestUrl;
        return configured.IsNotEmpty()
            ? configured
            : "https://www.gstatic.com/generate_204";
    }

    private async Task<int> ReportCandidateFailureAsync(
        FailoverRelayCandidate candidate,
        string protocol,
        string target,
        string reason)
    {
        var failureCount = IncrementDailyFailureCount(candidate.ProfileId);
        Logging.SaveFailoverLog(
            $"event=relay-health-confirm-failed profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} target={target} reason={reason} failuresToday={failureCount} action=mark-failed");
        await UpdateExternalFailoverStatusAsync(
            candidate.ProfileId,
            FailoverHealthStatus.Failed,
            reason,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return failureCount;
    }

    private Task ReportCandidateSelectedAsync(FailoverRelayCandidate candidate, string protocol, string target)
    {
        CurrentActiveProfileId = candidate.ProfileId;
        Logging.SaveFailoverLog(
            $"event=relay-candidate-selected profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} target={target} reason=selected action=mark-active");
        AppEvents.FailoverHealthChangedRequested.Publish();
        return Task.CompletedTask;
    }

    private async Task ReportCandidateStatusAsync(
        FailoverRelayCandidate candidate,
        string protocol,
        string target,
        string status,
        string? reason)
    {
        await UpdateExternalFailoverStatusAsync(
            candidate.ProfileId,
            status,
            reason,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private int IncrementDailyFailureCount(string profileId)
    {
        lock (_failureCountLock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (today != _failureCountDate)
            {
                _dailyFailureCounts.Clear();
                _failureCountDate = today;
            }

            _dailyFailureCounts.TryGetValue(profileId, out var count);
            count++;
            _dailyFailureCounts[profileId] = count;
            return count;
        }
    }

    private async Task UpdateExternalFailoverStatusAsync(
        string profileId,
        string status,
        string? failureReason,
        long now)
    {
        var groupId = AppManager.Instance.Config.ActiveFailoverGroupId;
        if (groupId.IsNullOrEmpty() || profileId.IsNullOrEmpty())
        {
            return;
        }

        await _statusWriteGate.WaitAsync();
        try
        {
            var snapshotKey = $"{groupId}:{profileId}:{status}:{failureReason}";
            if (_lastExternalStatusWrites.TryGetValue(snapshotKey, out var snapshot)
                && snapshot.Status == status
                && snapshot.FailureReason == failureReason
                && now - snapshot.UpdatedAt < 1000)
            {
                return;
            }

            var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId && item.SourceProfileId == profileId && item.Enabled)
                .ToListAsync();
            if (items.Count == 0)
            {
                return;
            }

            var changed = false;
            foreach (var item in items)
            {
                if (status == FailoverHealthStatus.Failed)
                {
                    item.FailureCount++;
                    item.LastFailureTime = now;
                    item.LastFailureReason = failureReason;
                    item.LastStatus = FailoverHealthStatus.Failed;
                    item.CooldownUntilTime = now + FailoverHealthStateMachine.GetCooldownMilliseconds(item.FailureCount);
                    changed = true;
                    continue;
                }

                if (status is FailoverHealthStatus.Requesting or FailoverHealthStatus.Degraded or FailoverHealthStatus.Fallback)
                {
                    if (item.LastStatus == status
                        && item.LastFailureReason == failureReason)
                    {
                        continue;
                    }

                    item.LastStatus = status;
                    item.LastFailureReason = failureReason;
                    changed = true;
                    continue;
                }

                if (item.LastStatus == FailoverHealthStatus.Normal
                    && item.LastFailureReason is null
                    && item.FailureCount == 0
                    && item.CooldownUntilTime is null
                    && item.LastSuccessTime.HasValue
                    && now - item.LastSuccessTime.Value < 1000)
                {
                    continue;
                }

                item.LastStatus = FailoverHealthStatus.Normal;
                item.FailureCount = 0;
                item.CooldownUntilTime = null;
                item.LastFailureReason = null;
                item.LastSuccessTime = now;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            await SQLiteHelper.Instance.UpdateAllAsync(items);
            _lastExternalStatusWrites[snapshotKey] = new ExternalStatusWriteSnapshot(status, failureReason, now);
            Logging.SaveFailoverLog(
                $"event=external-status-changed profileId={profileId} reason={failureReason ?? "none"} action={status}");
            AppEvents.FailoverHealthChangedRequested.Publish();
        }
        finally
        {
            _statusWriteGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync();
            _service = null;
        }
        CurrentListenPort = 0;
        CurrentActiveProfileId = null;
    }

    private sealed record ExternalStatusWriteSnapshot(string Status, string? FailureReason, long UpdatedAt);
}
