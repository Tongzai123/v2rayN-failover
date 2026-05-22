namespace ServiceLib.Services;

public interface IFailoverHealthProbe
{
    Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken);

    Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
        IReadOnlyList<ProfileItem> profiles,
        CancellationToken cancellationToken,
        Func<FailoverHealthProbeProgress, Task>? updateFunc = null);
}
