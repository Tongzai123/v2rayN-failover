namespace ServiceLib.Services;

public class RealPingProbeService
{
    private readonly Config _config;
    private readonly Func<List<ServerTestItem>, bool, Task<IAsyncDisposable?>> _loadCoreConfigSpeedtest;
    private readonly Func<ServerTestItem, bool, Task<IAsyncDisposable?>> _loadSingleCoreConfigSpeedtest;
    private readonly Func<string, IWebProxy, int, CancellationToken, Task<int>> _getRealPingTime;

    public RealPingProbeService(Config config)
        : this(
            config,
            async (items, suppressFailover) => ToSession(await CoreManager.Instance.LoadCoreConfigSpeedtest(items, suppressFailover)),
            async (item, suppressFailover) => ToSession(await CoreManager.Instance.LoadCoreConfigSpeedtest(item, suppressFailover)),
            ConnectionHandler.GetRealPingTime)
    {
    }

    internal RealPingProbeService(
        Config config,
        Func<List<ServerTestItem>, bool, Task<IAsyncDisposable?>> loadCoreConfigSpeedtest,
        Func<ServerTestItem, bool, Task<IAsyncDisposable?>> loadSingleCoreConfigSpeedtest,
        Func<string, IWebProxy, int, CancellationToken, Task<int>> getRealPingTime)
    {
        _config = config;
        _loadCoreConfigSpeedtest = loadCoreConfigSpeedtest;
        _loadSingleCoreConfigSpeedtest = loadSingleCoreConfigSpeedtest;
        _getRealPingTime = getRealPingTime;
    }

    public async Task<IReadOnlyList<RealPingProbeResult>> ProbeAsync(
        IReadOnlyList<ServerTestItem> testItems,
        bool suppressFailover,
        CancellationToken cancellationToken,
        Func<RealPingProbeResult, Task>? progressFunc = null)
    {
        if (testItems.Count == 0)
        {
            return [];
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return testItems.Select(item => RealPingProbeResult.Cancel(item.IndexId ?? string.Empty)).ToList();
        }

        var results = new Dictionary<int, RealPingProbeResult>();
        var pageSize = GetInitialPageSize(testItems.Count);
        await ProbePagedAsync(testItems.ToList(), pageSize, suppressFailover, results, cancellationToken, progressFunc);

        return testItems
            .Select(item => results.TryGetValue(item.QueueNum, out var result)
                ? result
                : RealPingProbeResult.Failure(item.IndexId ?? string.Empty, "request-failed"))
            .ToList();
    }

    private async Task ProbePagedAsync(
        List<ServerTestItem> testItems,
        int pageSize,
        bool suppressFailover,
        Dictionary<int, RealPingProbeResult> results,
        CancellationToken cancellationToken,
        Func<RealPingProbeResult, Task>? progressFunc)
    {
        foreach (var batch in testItems.Chunk(pageSize).Select(chunk => chunk.ToList()))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                MarkCancelled(testItems, results);
                return;
            }

            var batchResults = await ProbeBatchCoreAsync(batch, suppressFailover, cancellationToken, progressFunc);
            foreach (var result in batchResults)
            {
                var item = batch.First(x => x.IndexId == result.ProfileId);
                results[item.QueueNum] = result;
            }

            var retryItems = batch
                .Where(item => !results.TryGetValue(item.QueueNum, out var result) || IsRetryableFailure(result))
                .ToList();
            if (retryItems.Count == 0)
            {
                continue;
            }

            if (pageSize <= 1)
            {
                foreach (var item in retryItems)
                {
                    var result = await ProbeSingleCoreAsync(item, suppressFailover, cancellationToken);
                    results[item.QueueNum] = result;
                    await PublishProgress(progressFunc, result);
                }
                continue;
            }

            var nextPageSize = Math.Max(1, pageSize / 2);
            await ProbePagedAsync(retryItems, nextPageSize, suppressFailover, results, cancellationToken, progressFunc);
        }
    }

    private async Task<IReadOnlyList<RealPingProbeResult>> ProbeBatchCoreAsync(
        List<ServerTestItem> testItems,
        bool suppressFailover,
        CancellationToken cancellationToken,
        Func<RealPingProbeResult, Task>? progressFunc)
    {
        ResetBatchTestState(testItems);
        IAsyncDisposable? processSession = null;
        try
        {
            processSession = await _loadCoreConfigSpeedtest(testItems, suppressFailover);
            cancellationToken.ThrowIfCancellationRequested();
            if (processSession is null)
            {
                return testItems.Select(item => RealPingProbeResult.Failure(item.IndexId ?? string.Empty, "core-start-failed")).ToList();
            }

            await Task.Delay(1000, cancellationToken);
            var tasks = testItems.Select(async item =>
            {
                var result = await ProbeItemAsync(item, cancellationToken);
                if (result.IsSuccess)
                {
                    await PublishProgress(progressFunc, result);
                }
                return result;
            }).ToList();
            return await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            return testItems.Select(item => RealPingProbeResult.Cancel(item.IndexId ?? string.Empty)).ToList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(RealPingProbeService), ex);
            return testItems.Select(item => RealPingProbeResult.Failure(item.IndexId ?? string.Empty, "probe-failed")).ToList();
        }
        finally
        {
            if (processSession != null)
            {
                await processSession.DisposeAsync();
            }
        }
    }

    private async Task<RealPingProbeResult> ProbeSingleCoreAsync(
        ServerTestItem item,
        bool suppressFailover,
        CancellationToken cancellationToken)
    {
        item.AllowTest = false;
        IAsyncDisposable? processSession = null;
        try
        {
            processSession = await _loadSingleCoreConfigSpeedtest(item, suppressFailover);
            cancellationToken.ThrowIfCancellationRequested();
            if (processSession is null)
            {
                return RealPingProbeResult.Failure(item.IndexId ?? string.Empty, "core-start-failed");
            }

            await Task.Delay(1000, cancellationToken);
            return await ProbeItemAsync(item, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return RealPingProbeResult.Cancel(item.IndexId ?? string.Empty);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(RealPingProbeService), ex);
            return RealPingProbeResult.Failure(item.IndexId ?? string.Empty, "probe-failed");
        }
        finally
        {
            if (processSession != null)
            {
                await processSession.DisposeAsync();
            }
        }
    }

    private async Task<RealPingProbeResult> ProbeItemAsync(ServerTestItem item, CancellationToken cancellationToken)
    {
        if (!item.AllowTest)
        {
            return RealPingProbeResult.Failure(item.IndexId ?? string.Empty, "test-skipped");
        }

        try
        {
            var webProxy = new WebProxy($"socks5://{Global.Loopback}:{item.Port}");
            var delay = await _getRealPingTime(_config.SpeedTestItem?.SpeedPingTestUrl ?? string.Empty, webProxy, 10, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return delay > 0
                ? RealPingProbeResult.Success(item.IndexId ?? string.Empty, delay)
                : RealPingProbeResult.Failure(item.IndexId ?? string.Empty, "request-failed");
        }
        catch (OperationCanceledException)
        {
            return RealPingProbeResult.Cancel(item.IndexId ?? string.Empty);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(RealPingProbeService), ex);
            return RealPingProbeResult.Failure(item.IndexId ?? string.Empty, "request-failed");
        }
    }

    private static int GetInitialPageSize(int itemCount)
    {
        if (itemCount <= 0)
        {
            return 0;
        }

        return Math.Min(itemCount, Global.SpeedTestPageSize);
    }

    private static bool IsRetryableFailure(RealPingProbeResult result)
    {
        if (result.IsSuccess || result.Cancelled)
        {
            return false;
        }

        return result.FailureReason is
            "test-skipped"
            or "core-start-failed"
            or "request-failed"
            or "probe-failed";
    }

    private static IAsyncDisposable? ToSession(ProcessService? processService)
        => processService is null ? null : new ProcessServiceSession(processService);

    private static void ResetBatchTestState(IEnumerable<ServerTestItem> testItems)
    {
        foreach (var item in testItems)
        {
            item.AllowTest = false;
        }
    }

    private static void MarkCancelled(IEnumerable<ServerTestItem> testItems, Dictionary<int, RealPingProbeResult> results)
    {
        foreach (var item in testItems)
        {
            results[item.QueueNum] = RealPingProbeResult.Cancel(item.IndexId ?? string.Empty);
        }
    }

    private static async Task PublishProgress(Func<RealPingProbeResult, Task>? progressFunc, RealPingProbeResult result)
    {
        if (progressFunc is not null)
        {
            await progressFunc(result);
        }
    }

    private sealed class ProcessServiceSession(ProcessService processService) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await processService.StopAsync();
            processService.Dispose();
        }
    }
}
