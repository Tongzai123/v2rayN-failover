namespace ServiceLib.Services;

public class CoreFailoverHealthProbe : IFailoverHealthProbe
{
    private readonly RealPingProbeService _realPingProbeService;

    public CoreFailoverHealthProbe(Config config)
        : this(config, new RealPingProbeService(config))
    {
    }

    internal CoreFailoverHealthProbe(Config config, RealPingProbeService realPingProbeService)
    {
        _realPingProbeService = realPingProbeService;
    }

    public async Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
    {
        var results = await ProbeBatchAsync([profile], cancellationToken);
        return results.FirstOrDefault(result => result.ProfileId == profile.IndexId)?.Result
            ?? FailoverHealthProbeResult.Failure("request-failed");
    }

    public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
        IReadOnlyList<ProfileItem> profiles,
        CancellationToken cancellationToken,
        Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
    {
        if (profiles.Count == 0)
        {
            return [];
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return profiles
                .Select(profile => new FailoverHealthProbeBatchResult(profile.IndexId, FailoverHealthProbeResult.Cancel()))
                .ToList();
        }

        var results = new FailoverHealthProbeBatchResult?[profiles.Count];
        var testItems = new List<ServerTestItem>();

        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var coreType = AppManager.Instance.GetCoreType(profile, profile.ConfigType);
            if (!IsSupportedCore(coreType))
            {
                ProfileExManager.Instance.SetTestDelay(profile.IndexId, -1);
                var result = FailoverHealthProbeResult.Failure("core-unsupported");
                await UpdateResult(updateFunc, profile.IndexId, result);
                results[i] = new(profile.IndexId, result);
                continue;
            }

            testItems.Add(CreateServerTestItem(profile, i, coreType));
        }

        foreach (var group in testItems.GroupBy(item => item.CoreType))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                MarkGroupCancelled(testItems, results);
                break;
            }

            var groupItems = group.ToList();
            var realPingResults = await _realPingProbeService.ProbeAsync(
                groupItems,
                suppressFailover: true,
                cancellationToken,
                async progress =>
                {
                    var mapped = ToFailoverHealthProbeResult(progress);
                    if (!mapped.Cancelled)
                    {
                        await UpdateResult(updateFunc, progress.ProfileId, mapped);
                        ProfileExManager.Instance.SetTestDelay(progress.ProfileId, mapped.IsSuccess ? mapped.Delay : -1);
                    }
                });

            foreach (var item in groupItems)
            {
                var realPingResult = realPingResults.FirstOrDefault(result => result.ProfileId == item.IndexId);
                results[item.QueueNum] = realPingResult is not null
                    ? new FailoverHealthProbeBatchResult(item.IndexId, ToFailoverHealthProbeResult(realPingResult))
                    : await MarkItemFailed(item, "request-failed", updateFunc);
            }
        }

        return results
            .Select((result, index) => result ?? new(profiles[index].IndexId, FailoverHealthProbeResult.Failure("request-failed")))
            .ToList();
    }

    private static bool IsSupportedCore(ECoreType coreType)
        => coreType is ECoreType.Xray or ECoreType.sing_box;

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

    private static ServerTestItem CreateServerTestItem(ProfileItem profile, int queueNum, ECoreType coreType)
        => new()
        {
            IndexId = profile.IndexId,
            Address = profile.Address,
            Port = profile.Port,
            ConfigType = profile.ConfigType,
            AllowTest = false,
            QueueNum = queueNum,
            Profile = profile,
            CoreType = coreType,
        };

    private static async Task<FailoverHealthProbeBatchResult> MarkItemFailed(
        ServerTestItem item,
        string reason,
        Func<FailoverHealthProbeProgress, Task>? updateFunc)
    {
        var result = FailoverHealthProbeResult.Failure(reason);
        ProfileExManager.Instance.SetTestDelay(item.IndexId, -1);
        await UpdateResult(updateFunc, item.IndexId, result);
        return new(item.IndexId, result);
    }

    private static async Task UpdateResult(
        Func<FailoverHealthProbeProgress, Task>? updateFunc,
        string? indexId,
        FailoverHealthProbeResult result)
    {
        if (updateFunc is null || indexId.IsNullOrEmpty())
        {
            return;
        }

        await updateFunc(new FailoverHealthProbeProgress(indexId, result));
    }

    private static void MarkGroupCancelled(
        IEnumerable<ServerTestItem> testItems,
        FailoverHealthProbeBatchResult?[] results)
    {
        foreach (var item in testItems)
        {
            results[item.QueueNum] ??= new(item.IndexId, FailoverHealthProbeResult.Cancel());
        }
    }
}
