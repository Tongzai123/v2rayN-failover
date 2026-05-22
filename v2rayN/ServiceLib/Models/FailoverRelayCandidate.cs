namespace ServiceLib.Models;

public enum FailoverRelayCandidateState
{
    Healthy,
    Failed,
    Recovering
}

public sealed record FailoverRelayCandidate(
    string ProfileId,
    string InboundTag,
    string OutboundTag,
    int InboundPort,
    string DisplayName,
    string LastStatus = FailoverHealthStatus.Unknown,
    long? LastSuccessTime = null,
    string? LastFailureReason = null,
    int Sort = 0);
