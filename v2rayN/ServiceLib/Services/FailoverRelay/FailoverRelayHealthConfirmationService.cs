namespace ServiceLib.Services.FailoverRelay;

public sealed class FailoverRelayHealthConfirmationService
{
    private readonly object _lock = new();
    private readonly FailoverRelayOptions _options;
    private readonly CancellationToken _lifecycleToken;
    private readonly Dictionary<string, CandidateHealthState> _states;

    public FailoverRelayHealthConfirmationService(
        FailoverRelayOptions options,
        IEnumerable<FailoverRelayCandidate> candidates,
        CancellationToken lifecycleToken)
    {
        _options = options;
        _lifecycleToken = lifecycleToken;
        _states = candidates.ToDictionary(
            candidate => candidate.ProfileId,
            candidate => new CandidateHealthState
            {
                LastSuccessTime = candidate.LastStatus == FailoverHealthStatus.Normal
                    ? candidate.LastSuccessTime
                    : null,
                ExternalStatus = candidate.LastStatus,
                LastFailureReason = candidate.LastFailureReason,
            });
    }

    public bool IsHealthConfirmationConfigured => GetProbeUrls().Count > 0;

    public Task<bool> ShouldUseBeforeRequestAsync(FailoverRelayCandidate candidate, CancellationToken requestToken)
    {
        return CheckBeforeRequestAsync(candidate, requestToken).ContinueWith(
            task => task.Result.ShouldUse,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task<FailoverRelayCandidatePrecheckResult> CheckBeforeRequestAsync(FailoverRelayCandidate candidate, CancellationToken requestToken)
    {
        var now = Now();
        var state = GetState(candidate.ProfileId);

        lock (_lock)
        {
            if (state.ExternalStatus == FailoverHealthStatus.Failed)
            {
                return new FailoverRelayCandidatePrecheckResult(false, false, "external-failed");
            }

            if (IsFreshSuccess(state, now))
            {
                return new FailoverRelayCandidatePrecheckResult(true, false, "fresh-success");
            }

            if (IsRecentSuccess(state, now))
            {
                StartConfirmationLocked(candidate);
                return new FailoverRelayCandidatePrecheckResult(true, false, "recent-success-refreshing");
            }

            return new FailoverRelayCandidatePrecheckResult(true, false, "health-confirmation-deferred");
        }
    }

    public async Task<FailoverRelayHealthConfirmationResult> ConfirmAfterRequestFailureAsync(
        FailoverRelayCandidate candidate,
        CancellationToken requestToken)
    {
        if (!IsHealthConfirmationConfigured)
        {
            return new FailoverRelayHealthConfirmationResult(
                candidate.ProfileId,
                false,
                "health-confirmation-not-configured",
                null,
                Now(),
                _options.HealthConfirmationFailureThreshold,
                true);
        }

        await WaitForFailureWindowDelayAsync(candidate, requestToken);
        return await WaitForConfirmationAsync(candidate, requestToken);
    }

    public Task<FailoverRelayHealthConfirmationResult> ConfirmUntilThresholdAsync(
        FailoverRelayCandidate candidate,
        CancellationToken requestToken)
    {
        if (!IsHealthConfirmationConfigured)
        {
            return Task.FromResult(new FailoverRelayHealthConfirmationResult(
                candidate.ProfileId,
                false,
                "health-confirmation-not-configured",
                null,
                Now(),
                0,
                false));
        }

        lock (_lock)
        {
            var state = GetState(candidate.ProfileId);
            if (state.ThresholdInFlight is not null)
            {
                return state.ThresholdInFlight;
            }

            var task = ConfirmUntilThresholdCoreAsync(candidate, requestToken);
            state.ThresholdInFlight = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_lock)
                    {
                        if (state.ThresholdInFlight == task)
                        {
                            state.ThresholdInFlight = null;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    public void MarkHealthRecovered(string profileId)
    {
        var now = Now();
        var state = GetState(profileId);
        lock (_lock)
        {
            state.ExternalStatus = FailoverHealthStatus.Normal;
            state.LastSuccessTime = now;
            state.ConsecutiveFailures = 0;
            state.LastFailureReason = null;
            state.FailureReportConsumed = false;
            state.LastFailureWindowTime = null;
            state.RecoveryGeneration++;
        }
    }

    public void MarkExternalFailed(string profileId, string? reason)
    {
        var state = GetState(profileId);
        lock (_lock)
        {
            state.ExternalStatus = FailoverHealthStatus.Failed;
            state.LastFailureReason = reason;
        }
    }

    public void MarkRequestSuccess(string profileId)
    {
        MarkHealthRecovered(profileId);
    }

    public bool TryConsumeFailureReport(FailoverRelayHealthConfirmationResult result)
    {
        if (!result.FailureThresholdReached)
        {
            return false;
        }

        var state = GetState(result.ProfileId);
        lock (_lock)
        {
            if (state.ConsecutiveFailures < _options.HealthConfirmationFailureThreshold
                || state.FailureReportConsumed)
            {
                return false;
            }

            state.FailureReportConsumed = true;
            return true;
        }
    }

    private async Task<FailoverRelayCandidatePrecheckResult> ConfirmBeforeUseAsync(FailoverRelayCandidate candidate, CancellationToken requestToken)
    {
        FailoverRelayHealthConfirmationResult? last = null;
        var maxAttempts = Math.Max(1, _options.HealthConfirmationFailureThreshold);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            last = await WaitForConfirmationAsync(candidate, requestToken);
            if (last.Success)
            {
                return new FailoverRelayCandidatePrecheckResult(true, false, last.Reason);
            }

            if (last.FailureThresholdReached)
            {
                return new FailoverRelayCandidatePrecheckResult(false, true, last.Reason);
            }

            if (attempt + 1 < maxAttempts && _options.HealthConfirmationFailureWindowDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.HealthConfirmationFailureWindowDelay, requestToken);
            }
        }

        return new FailoverRelayCandidatePrecheckResult(false, last?.FailureThresholdReached ?? false, last?.Reason ?? "health-confirmation-failed");
    }

    private async Task<FailoverRelayHealthConfirmationResult> ConfirmUntilThresholdCoreAsync(
        FailoverRelayCandidate candidate,
        CancellationToken requestToken)
    {
        FailoverRelayHealthConfirmationResult? last = null;
        var maxAttempts = Math.Max(1, _options.HealthConfirmationFailureThreshold);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            last = await WaitForConfirmationAsync(candidate, requestToken);
            if (last.Success || last.FailureThresholdReached)
            {
                return last;
            }

            if (attempt + 1 < maxAttempts && _options.HealthConfirmationFailureWindowDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.HealthConfirmationFailureWindowDelay, requestToken);
            }
        }

        return last ?? new FailoverRelayHealthConfirmationResult(
            candidate.ProfileId,
            false,
            "health-confirmation-not-run",
            null,
            Now(),
            0,
            false);
    }

    private Task<FailoverRelayHealthConfirmationResult> StartConfirmationLocked(FailoverRelayCandidate candidate)
    {
        var state = GetState(candidate.ProfileId);
        if (state.InFlight is not null)
        {
            return state.InFlight;
        }

        var task = RunConfirmationAsync(candidate);
        state.InFlight = task;
        _ = task.ContinueWith(
            _ =>
            {
                lock (_lock)
                {
                    if (state.InFlight == task)
                    {
                        state.InFlight = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private Task<FailoverRelayHealthConfirmationResult> GetOrStartConfirmationAsync(FailoverRelayCandidate candidate)
    {
        lock (_lock)
        {
            return StartConfirmationLocked(candidate);
        }
    }

    private async Task<FailoverRelayHealthConfirmationResult> WaitForConfirmationAsync(
        FailoverRelayCandidate candidate,
        CancellationToken requestToken)
    {
        var task = GetOrStartConfirmationAsync(candidate);
        try
        {
            return await task.WaitAsync(requestToken);
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            return new FailoverRelayHealthConfirmationResult(
                candidate.ProfileId,
                false,
                "request-cancelled",
                null,
                Now(),
                GetState(candidate.ProfileId).ConsecutiveFailures,
                false);
        }
    }

    private async Task WaitForFailureWindowDelayAsync(FailoverRelayCandidate candidate, CancellationToken requestToken)
    {
        var delay = TimeSpan.Zero;
        lock (_lock)
        {
            var state = GetState(candidate.ProfileId);
            if (state.InFlight is not null
                || state.ExternalStatus == FailoverHealthStatus.Failed
                || state.ConsecutiveFailures <= 0
                || !state.LastFailureWindowTime.HasValue
                || _options.HealthConfirmationFailureWindowDelay <= TimeSpan.Zero)
            {
                return;
            }

            var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, Now() - state.LastFailureWindowTime.Value));
            delay = _options.HealthConfirmationFailureWindowDelay - elapsed;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, requestToken);
        }
    }

    private async Task<FailoverRelayHealthConfirmationResult> RunConfirmationAsync(FailoverRelayCandidate candidate)
    {
        var state = GetState(candidate.ProfileId);
        long recoveryGeneration;
        lock (_lock)
        {
            recoveryGeneration = state.RecoveryGeneration;
        }

        var probe = await ProbeAsync(candidate);
        var completedAt = Now();

        lock (_lock)
        {
            if (state.RecoveryGeneration != recoveryGeneration)
            {
                return new FailoverRelayHealthConfirmationResult(
                    candidate.ProfileId,
                    true,
                    "stale-after-recovery",
                    probe.Delay,
                    completedAt,
                    state.ConsecutiveFailures,
                    false);
            }

            if (probe.Success)
            {
                state.ExternalStatus = FailoverHealthStatus.Normal;
                state.LastSuccessTime = completedAt;
                state.ConsecutiveFailures = 0;
                state.LastFailureReason = null;
                state.FailureReportConsumed = false;
                state.LastFailureWindowTime = null;
                return new FailoverRelayHealthConfirmationResult(
                    candidate.ProfileId,
                    true,
                    probe.Reason,
                    probe.Delay,
                    completedAt,
                    0,
                    false);
            }

            state.ConsecutiveFailures++;
            state.LastFailureReason = probe.Reason;
            state.LastFailureWindowTime = completedAt;
            var thresholdReached = state.ConsecutiveFailures >= _options.HealthConfirmationFailureThreshold;
            if (thresholdReached)
            {
                state.ExternalStatus = FailoverHealthStatus.Failed;
            }

            return new FailoverRelayHealthConfirmationResult(
                candidate.ProfileId,
                false,
                probe.Reason,
                probe.Delay,
                completedAt,
                state.ConsecutiveFailures,
                thresholdReached);
        }
    }

    private async Task<FailoverRelayHealthProbeResult> ProbeAsync(FailoverRelayCandidate candidate)
    {
        var probeUrls = GetProbeUrls();
        if (probeUrls.Count == 0)
        {
            return new FailoverRelayHealthProbeResult(false, "probe-url-empty");
        }

        var probes = await Task.WhenAll(probeUrls.Select(url => ProbeUrlAsync(candidate, url)));
        var success = probes
            .Where(probe => probe.Result.Success)
            .OrderBy(probe => probe.Result.Delay ?? int.MaxValue)
            .FirstOrDefault();
        if (success is not null)
        {
            var failedCount = probes.Count(probe => !probe.Result.Success);
            return success.Result with
            {
                Reason = $"ok url={success.Url} failedUrls={failedCount}/{probeUrls.Count}",
            };
        }

        var reasonSummary = string.Join(
            ",",
            probes
                .GroupBy(probe => probe.Result.Reason)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Count()}"));
        return new FailoverRelayHealthProbeResult(false, $"all-probes-failed {reasonSummary}");
    }

    private async Task<ProbeUrlResult> ProbeUrlAsync(FailoverRelayCandidate candidate, string probeUrl)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleToken);
            timeoutCts.CancelAfter(_options.HealthConfirmationTimeout);

            if (_options.HealthProbeAsync is not null)
            {
                var result = await _options.HealthProbeAsync(
                    candidate,
                    probeUrl,
                    _options.HealthConfirmationTimeout,
                    timeoutCts.Token);
                return new ProbeUrlResult(probeUrl, result);
            }

            var resultFromCandidate = await ProbeThroughCandidateAsync(candidate, probeUrl, timeoutCts.Token);
            return new ProbeUrlResult(probeUrl, resultFromCandidate);
        }
        catch (OperationCanceledException) when (_lifecycleToken.IsCancellationRequested)
        {
            return new ProbeUrlResult(probeUrl, new FailoverRelayHealthProbeResult(false, "cancelled"));
        }
        catch (OperationCanceledException)
        {
            return new ProbeUrlResult(probeUrl, new FailoverRelayHealthProbeResult(false, "timeout"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            return new ProbeUrlResult(probeUrl, new FailoverRelayHealthProbeResult(false, ex.GetType().Name));
        }
    }

    private async Task<FailoverRelayHealthProbeResult> ProbeThroughCandidateAsync(
        FailoverRelayCandidate candidate,
        string probeUrl,
        CancellationToken cancellationToken)
    {
        var proxy = new WebProxy($"socks5://{Global.Loopback}:{candidate.InboundPort}");
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AllowAutoRedirect = false,
        });
        var timer = Stopwatch.StartNew();
        using var response = await client.GetAsync(
            probeUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        timer.Stop();
        var delay = Math.Max(1, (int)timer.Elapsed.TotalMilliseconds);
        var statusCode = (int)response.StatusCode;
        return _options.HealthConfirmationSuccessStatusCodes.Contains(statusCode)
            ? new FailoverRelayHealthProbeResult(true, $"http-{statusCode}", delay)
            : new FailoverRelayHealthProbeResult(false, $"http-{statusCode}", delay);
    }

    private CandidateHealthState GetState(string profileId)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(profileId, out var state))
            {
                return state;
            }

            state = new CandidateHealthState();
            _states[profileId] = state;
            return state;
        }
    }

    private IReadOnlyList<string> GetProbeUrls()
    {
        if (_options.HealthConfirmationProbeUrls.Count > 0)
        {
            return _options.HealthConfirmationProbeUrls
                .Where(url => url.IsNotEmpty())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return _options.SpeedPingTestUrl.IsNotEmpty()
            ? [_options.SpeedPingTestUrl]
            : [];
    }

    private bool IsFreshSuccess(CandidateHealthState state, long now)
    {
        return state.LastSuccessTime.HasValue
            && now - state.LastSuccessTime.Value <= (long)_options.HealthSuccessCacheDuration.TotalMilliseconds;
    }

    private bool IsRecentSuccess(CandidateHealthState state, long now)
    {
        return state.LastSuccessTime.HasValue
            && now - state.LastSuccessTime.Value <= (long)_options.HealthRecentSuccessDuration.TotalMilliseconds;
    }

    private static long Now()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private sealed class CandidateHealthState
    {
        public string ExternalStatus { get; set; } = FailoverHealthStatus.Unknown;
        public long? LastSuccessTime { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? LastFailureReason { get; set; }
        public long? LastFailureWindowTime { get; set; }
        public Task<FailoverRelayHealthConfirmationResult>? InFlight { get; set; }
        public Task<FailoverRelayHealthConfirmationResult>? ThresholdInFlight { get; set; }
        public bool FailureReportConsumed { get; set; }
        public long RecoveryGeneration { get; set; }
    }

    private sealed record ProbeUrlResult(string Url, FailoverRelayHealthProbeResult Result);
}

public sealed record FailoverRelayCandidatePrecheckResult(
    bool ShouldUse,
    bool FailureThresholdReached,
    string Reason);
