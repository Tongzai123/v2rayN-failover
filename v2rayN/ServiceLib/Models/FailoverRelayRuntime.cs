namespace ServiceLib.Models;

public sealed record FailoverRelayRuntime
{
    public int ListenPort { get; init; }
    public IReadOnlyList<FailoverRelayCandidate> Candidates { get; init; } = [];
    public bool Enabled => Candidates.Count > 0;
}
