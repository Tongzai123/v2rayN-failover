using ServiceLib.Handler.Builder;
using ServiceLib.Manager;

namespace ServiceLib.Models;

public enum FailoverRuntimeKind
{
    None = 0,
    VirtualPolicyGroup = 1,
    DirectProfile = 2,
}

public record FailoverRuntimeResolveResult(
    FailoverRuntimeKind Kind,
    ProfileItem? EffectiveNode,
    List<ProfileItem> QueueProfiles,
    string? RuntimeTargetProfileId,
    NodeValidatorResult ValidatorResult)
{
    public bool Success => ValidatorResult.Success;

    public string RuntimeQueueSignature => Kind == FailoverRuntimeKind.VirtualPolicyGroup
        ? FailoverGroupManager.BuildRuntimeQueueSignature(QueueProfiles)
        : RuntimeTargetProfileId ?? string.Empty;
}
