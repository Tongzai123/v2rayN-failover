namespace ServiceLib.Services;

public class FailoverHealthService
{
    private readonly Config _config;
    private readonly IFailoverHealthProbe _probe;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _probeCts;
    private Task? _loopTask;
    private bool _restartAfterCurrentLoopStops;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly TimeSpan _quickSwitchAfter;
    private readonly TimeSpan _probeRoundTimeout;
    private readonly TimeSpan _probeLoopInterval;
    private readonly object _probingPreviousStatusLock = new();
    private readonly Dictionary<string, string> _probingPreviousStatuses = [];
    private string? _pendingLeastDelayReloadTargetProfileId;

    public static FailoverHealthService Instance { get; } =
        new(AppManager.Instance.Config, new CoreFailoverHealthProbe(AppManager.Instance.Config));

    public FailoverHealthService(Config config, IFailoverHealthProbe probe)
    {
        _config = config;
        _probe = probe;
        _quickSwitchAfter = FailoverHealthTiming.LeastDelayQuickSwitchAfter;
        _probeRoundTimeout = FailoverHealthTiming.ProbeRoundTimeout;
        _probeLoopInterval = FailoverHealthTiming.ProbeLoopInterval;
    }

    internal FailoverHealthService(
        Config config,
        IFailoverHealthProbe probe,
        TimeSpan quickSwitchAfter,
        TimeSpan probeRoundTimeout,
        TimeSpan probeLoopInterval)
        : this(config, probe)
    {
        _quickSwitchAfter = quickSwitchAfter;
        _probeRoundTimeout = probeRoundTimeout;
        _probeLoopInterval = probeLoopInterval;
    }

    public void Start()
    {
        ConfigHandler.NormalizeFailoverMode(_config);
        if (_config.FailoverMode == EFailoverMode.Off)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_loopTask?.IsCompleted == false)
            {
                if (_loopCts?.IsCancellationRequested == true)
                {
                    _restartAfterCurrentLoopStops = true;
                }
                return;
            }

            StartLoopLocked();
        }
    }

    public void Stop(bool cancelCurrentProbe = false)
    {
        lock (_syncRoot)
        {
            _restartAfterCurrentLoopStops = false;
            _loopCts?.Cancel();
            if (cancelCurrentProbe)
            {
                _probeCts?.Cancel();
            }
        }
    }

    public async Task StopAndClearProbingAsync()
        => await StopAndRestoreProbingAsync();

    public async Task StopAndRestoreProbingAsync()
    {
        Stop(cancelCurrentProbe: true);
        await RestoreActiveGroupProbingStatusAsync();
    }

    public void StartLeastDelayProbeRound()
    {
        CancellationToken token;
        lock (_syncRoot)
        {
            _restartAfterCurrentLoopStops = false;
            _loopCts?.Cancel();
            _probeCts?.Cancel();
            _probeCts = new CancellationTokenSource();
            token = _probeCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLeastDelayProbeRoundAsync(token);
                await Task.Delay(_probeLoopInterval, token);
                Start();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        });
    }

    private void StartLoopLocked()
    {
        _loopCts?.Dispose();
        _probeCts?.Dispose();
        _loopCts = new CancellationTokenSource();
        _probeCts = new CancellationTokenSource();
        _restartAfterCurrentLoopStops = false;
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    public Task CheckOnceAsync()
        => CheckOnceAsync(_probeCts?.Token ?? CancellationToken.None);

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
        => await CheckActiveGroupOnceAsync(force: false, reloadOnLeastDelayChange: true, cancellationToken);

    public async Task ApplyManualRealPingResultsAsync(IReadOnlyList<RealPingProbeResult> results)
    {
        if (results.Count == 0
            || _config.FailoverMode == EFailoverMode.Off
            || _config.ActiveFailoverGroupId.IsNullOrEmpty())
        {
            return;
        }

        await _probeGate.WaitAsync();
        try
        {
            if (_config.FailoverMode == EFailoverMode.Off || _config.ActiveFailoverGroupId.IsNullOrEmpty())
            {
                return;
            }

            var resultMap = results
                .Where(result => result.ProfileId.IsNotEmpty() && !result.Cancelled)
                .GroupBy(result => result.ProfileId)
                .ToDictionary(group => group.Key, group => group.First());
            if (resultMap.Count == 0)
            {
                return;
            }

            var entries = await FailoverGroupManager.GetQueueEntries(_config.ActiveFailoverGroupId, true);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var changedItems = new List<FailoverGroupItem>();
            foreach (var entry in entries)
            {
                if (!resultMap.TryGetValue(entry.Profile.IndexId, out var result))
                {
                    continue;
                }

                FailoverHealthStateMachine.ApplyProbeResult(entry.Item, ToFailoverHealthProbeResult(result), now);
                if (result.IsSuccess)
                {
                    FailoverRelayManager.Instance.MarkHealthRecovered(entry.Profile.IndexId);
                }
                changedItems.Add(entry.Item);
            }

            if (changedItems.Count == 0)
            {
                return;
            }

            await SQLiteHelper.Instance.UpdateAllAsync(changedItems);
            AppEvents.FailoverHealthChangedRequested.Publish();
            if (_config.FailoverMode == EFailoverMode.LeastDelay)
            {
                await ReloadLeastDelayBestTargetIfAvailable();
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public Task CheckActiveGroupOnceAsync(bool force, bool reloadOnLeastDelayChange)
        => CheckActiveGroupOnceAsync(force, reloadOnLeastDelayChange, _probeCts?.Token ?? CancellationToken.None);

    public async Task CheckActiveGroupOnceAsync(
        bool force,
        bool reloadOnLeastDelayChange,
        CancellationToken cancellationToken,
        Func<bool>? cancellationMeansTimeout = null)
    {
        if (_config.FailoverMode == EFailoverMode.Off || _config.ActiveFailoverGroupId.IsNullOrEmpty())
        {
            return;
        }

        if (!_probeGate.Wait(0))
        {
            return;
        }

        var startedProbeItemIds = new List<string>();
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var currentNode = reloadOnLeastDelayChange
                ? await AppManager.Instance.GetProfileItem(_config.IndexId)
                : null;
            var runtimeSignatureBefore = currentNode != null
                ? await FailoverGroupManager.GetRuntimeQueueSignature(_config, currentNode)
                : null;
            var entries = force
                ? await FailoverGroupManager.GetQueueEntries(_config.ActiveFailoverGroupId, true)
                : await FailoverGroupManager.GetDueHealthCheckEntries(_config.ActiveFailoverGroupId, now);
            var probeEntries = new List<(FailoverQueueEntry Entry, string PreviousStatus)>();
            foreach (var entry in entries)
            {
                var previousStatus = entry.Item.LastStatus;
                if (!FailoverHealthStateMachine.TryMarkProbeStarting(entry.Item, now, force))
                {
                    continue;
                }

                probeEntries.Add((entry, previousStatus));
                startedProbeItemIds.Add(entry.Item.Id);
                RememberProbingPreviousStatus(entry.Item.Id, previousStatus);
            }

            if (probeEntries.Count == 0)
            {
                return;
            }

            await SQLiteHelper.Instance.UpdateAllAsync(probeEntries.Select(x => x.Entry.Item));
            AppEvents.FailoverHealthChangedRequested.Publish();
            foreach (var probeEntry in probeEntries)
            {
                await PublishSpeedTestResult(probeEntry.Entry.Profile.IndexId, ResUI.Speedtesting);
            }

            var appliedProgressProfileIds = new HashSet<string>();
            var results = await ProbeAsync(
                probeEntries.Select(x => x.Entry.Profile).ToList(),
                cancellationToken,
                async progress =>
                {
                    if (appliedProgressProfileIds.Add(progress.ProfileId))
                    {
                        await ApplyProbeProgress(probeEntries, progress);
                    }
                },
                cancellationMeansTimeout);
            foreach (var probeEntry in probeEntries)
            {
                var entry = probeEntry.Entry;
                if (appliedProgressProfileIds.Contains(entry.Profile.IndexId))
                {
                    continue;
                }

                var result = results.TryGetValue(entry.Profile.IndexId, out var found)
                    ? found
                    : FailoverHealthProbeResult.Failure("request-failed");
                if (result.Cancelled)
                {
                    if (cancellationMeansTimeout?.Invoke() == true)
                    {
                        result = FailoverHealthProbeResult.Failure("probe-timeout");
                    }
                    else
                    {
                        entry.Item.LastStatus = NormalizeRestoredProbingStatus(probeEntry.PreviousStatus);
                        continue;
                    }
                }

                FailoverHealthStateMachine.ApplyProbeResult(entry.Item, result, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (result.IsSuccess)
                {
                    FailoverRelayManager.Instance.MarkHealthRecovered(entry.Profile.IndexId);
                }
            }

            await SQLiteHelper.Instance.UpdateAllAsync(probeEntries.Select(x => x.Entry.Item));
            AppEvents.FailoverHealthChangedRequested.Publish();

            if (reloadOnLeastDelayChange && currentNode != null)
            {
                var runtimeSignatureAfter = await FailoverGroupManager.GetRuntimeQueueSignature(_config, currentNode);
                if (runtimeSignatureAfter.IsNotEmpty() && runtimeSignatureAfter != runtimeSignatureBefore)
                {
                    AppEvents.ReloadRequested.Publish();
                }
            }
        }
        finally
        {
            ForgetProbingPreviousStatuses(startedProbeItemIds);
            _probeGate.Release();
        }
    }

    private async Task<Dictionary<string, FailoverHealthProbeResult>> ProbeAsync(
        IReadOnlyList<ProfileItem> profiles,
        CancellationToken cancellationToken,
        Func<FailoverHealthProbeProgress, Task>? updateFunc,
        Func<bool>? cancellationMeansTimeout = null)
    {
        try
        {
            var probeTask = _probe.ProbeBatchAsync(
                profiles,
                cancellationToken,
                updateFunc);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completedTask = await Task.WhenAny(probeTask, cancellationTask);
            if (completedTask != probeTask)
            {
                return CreateCancelledProbeResults(profiles, cancellationMeansTimeout);
            }

            var results = await probeTask;
            return results
                .GroupBy(x => x.ProfileId)
                .ToDictionary(group => group.Key, group => group.First().Result);
        }
        catch (OperationCanceledException)
        {
            return CreateCancelledProbeResults(profiles, cancellationMeansTimeout);
        }
    }

    private static Dictionary<string, FailoverHealthProbeResult> CreateCancelledProbeResults(
        IReadOnlyList<ProfileItem> profiles,
        Func<bool>? cancellationMeansTimeout)
    {
        return profiles
            .GroupBy(x => x.IndexId)
            .ToDictionary(
                group => group.Key,
                _ => cancellationMeansTimeout?.Invoke() == true
                    ? FailoverHealthProbeResult.Failure("probe-timeout")
                    : FailoverHealthProbeResult.Cancel());
    }

    private static async Task ApplyProbeProgress(
        IReadOnlyList<(FailoverQueueEntry Entry, string PreviousStatus)> probeEntries,
        FailoverHealthProbeProgress progress)
    {
        var matchedEntries = probeEntries
            .Where(probeEntry => probeEntry.Entry.Profile.IndexId == progress.ProfileId)
            .ToList();
        if (matchedEntries.Count == 0 || progress.Result.Cancelled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var probeEntry in matchedEntries)
        {
            FailoverHealthStateMachine.ApplyProbeResult(probeEntry.Entry.Item, progress.Result, now);
            if (progress.Result.IsSuccess)
            {
                FailoverRelayManager.Instance.MarkHealthRecovered(probeEntry.Entry.Profile.IndexId);
            }
        }

        await SQLiteHelper.Instance.UpdateAllAsync(matchedEntries.Select(x => x.Entry.Item));
        AppEvents.FailoverHealthChangedRequested.Publish();
        await PublishSpeedTestResult(
            progress.ProfileId,
            progress.Result.IsSuccess ? progress.Result.Delay.ToString() : "-1");
    }

    private static async Task PublishSpeedTestResult(string? indexId, string? delay)
    {
        AppEvents.SpeedTestResultRequested.Publish(new SpeedTestResult
        {
            IndexId = indexId,
            Delay = delay,
        });
        await Task.CompletedTask;
    }

    private static FailoverHealthProbeResult ToFailoverHealthProbeResult(RealPingProbeResult result)
    {
        if (result.Cancelled)
        {
            return FailoverHealthProbeResult.Cancel();
        }

        return result.IsSuccess
            ? FailoverHealthProbeResult.Success(result.Delay)
            : FailoverHealthProbeResult.Failure(result.FailureReason ?? "request-failed");
    }

    private async Task RestoreActiveGroupProbingStatusAsync()
    {
        if (_config.ActiveFailoverGroupId.IsNullOrEmpty())
        {
            return;
        }

        var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == _config.ActiveFailoverGroupId
                && item.Enabled
                && item.LastStatus == FailoverHealthStatus.Probing)
            .ToListAsync();
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            item.LastStatus = GetProbingPreviousStatus(item.Id) ?? FailoverHealthStatus.Unknown;
        }

        await SQLiteHelper.Instance.UpdateAllAsync(items);
        AppEvents.FailoverHealthChangedRequested.Publish();
    }

    private void RememberProbingPreviousStatus(string itemId, string previousStatus)
    {
        if (itemId.IsNullOrEmpty())
        {
            return;
        }

        lock (_probingPreviousStatusLock)
        {
            _probingPreviousStatuses[itemId] = NormalizeRestoredProbingStatus(previousStatus);
        }
    }

    private string? GetProbingPreviousStatus(string itemId)
    {
        if (itemId.IsNullOrEmpty())
        {
            return null;
        }

        lock (_probingPreviousStatusLock)
        {
            return _probingPreviousStatuses.GetValueOrDefault(itemId);
        }
    }

    private void ForgetProbingPreviousStatuses(IEnumerable<string> itemIds)
    {
        lock (_probingPreviousStatusLock)
        {
            foreach (var itemId in itemIds)
            {
                _probingPreviousStatuses.Remove(itemId);
            }
        }
    }

    private static string NormalizeRestoredProbingStatus(string? status)
    {
        return status switch
        {
            FailoverHealthStatus.Normal => FailoverHealthStatus.Normal,
            FailoverHealthStatus.Failed => FailoverHealthStatus.Failed,
            FailoverHealthStatus.Requesting => FailoverHealthStatus.Requesting,
            FailoverHealthStatus.Degraded => FailoverHealthStatus.Degraded,
            FailoverHealthStatus.Fallback => FailoverHealthStatus.Fallback,
            _ => FailoverHealthStatus.Unknown,
        };
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await CheckOnceAsync(_probeCts?.Token ?? CancellationToken.None);
                await Task.Delay(_probeLoopInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            RestartLoopIfRequested();
        }
    }

    private async Task RunLeastDelayProbeRoundAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_probeRoundTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        bool IsRoundTimeout() => timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

        var probeTask = CheckActiveGroupOnceAsync(
            force: true,
            reloadOnLeastDelayChange: false,
            linkedCts.Token,
            cancellationMeansTimeout: IsRoundTimeout);
        var quickTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_quickSwitchAfter, linkedCts.Token);
                await ReloadLeastDelayBestTargetIfAvailable();
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
            }
        }, linkedCts.Token);

        await Task.WhenAny(probeTask, quickTask);
        await probeTask;
        await ReloadLeastDelayBestTargetIfAvailable();
    }

    private async Task ReloadLeastDelayBestTargetIfAvailable()
    {
        if (_config.FailoverMode != EFailoverMode.LeastDelay || _config.ActiveFailoverGroupId.IsNullOrEmpty())
        {
            return;
        }

        var target = await FailoverGroupManager.GetLeastDelayRuntimeTargetProfileId(_config.ActiveFailoverGroupId);
        if (target.IsNullOrEmpty())
        {
            return;
        }

        var currentNode = await AppManager.Instance.GetProfileItem(_config.IndexId);
        var queueSignature = currentNode != null
            ? await FailoverGroupManager.GetRuntimeQueueSignature(_config, currentNode)
            : null;

        if (AppManager.Instance.RunningFailoverTargetProfileId == target
            && AppManager.Instance.RunningFailoverQueueSignature == queueSignature)
        {
            _pendingLeastDelayReloadTargetProfileId = null;
            return;
        }

        if (_config.FailoverStartupProfileId == target
            && _pendingLeastDelayReloadTargetProfileId == target
            && AppManager.Instance.RunningFailoverQueueSignature == queueSignature)
        {
            return;
        }

        _config.FailoverStartupProfileId = target;
        _pendingLeastDelayReloadTargetProfileId = target;
        AppEvents.FailoverHealthChangedRequested.Publish();
        AppEvents.ReloadRequested.Publish();
    }

    private void RestartLoopIfRequested()
    {
        lock (_syncRoot)
        {
            if (!_restartAfterCurrentLoopStops || _config.FailoverMode == EFailoverMode.Off)
            {
                _restartAfterCurrentLoopStops = false;
                return;
            }

            StartLoopLocked();
        }
    }
}
