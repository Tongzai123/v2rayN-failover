namespace ServiceLib.Services.FailoverRelay;

public sealed class FailoverCandidateStateStore
{
    private readonly object _lock = new();
    private readonly List<FailoverRelayCandidate> _candidates;
    private readonly Dictionary<string, FailoverRelayCandidateState> _states;

    public FailoverCandidateStateStore(IEnumerable<FailoverRelayCandidate> candidates)
    {
        _candidates = candidates.ToList();
        _states = _candidates.ToDictionary(
            x => x.ProfileId,
            x => x.LastStatus == FailoverHealthStatus.Failed
                ? FailoverRelayCandidateState.Failed
                : FailoverRelayCandidateState.Healthy);
    }

    public bool AllCandidatesFailed
    {
        get
        {
            lock (_lock)
            {
                return _states.Count > 0 && _states.Values.All(x => x == FailoverRelayCandidateState.Failed);
            }
        }
    }

    public FailoverRelayCandidateState GetState(string profileId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(profileId, out var state) ? state : FailoverRelayCandidateState.Failed;
        }
    }

    public IReadOnlyList<FailoverRelayCandidate> GetAllCandidates()
    {
        lock (_lock)
        {
            return _candidates.ToList();
        }
    }

    public IReadOnlyList<FailoverRelayCandidate> GetRequestCandidates()
    {
        lock (_lock)
        {
            var available = _candidates
                .Where(x => _states.GetValueOrDefault(x.ProfileId) != FailoverRelayCandidateState.Failed)
                .ToList();
            return available.Count > 0 ? available : _candidates.ToList();
        }
    }

    public void MarkRequestFailure(string profileId)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(profileId))
            {
                _states[profileId] = FailoverRelayCandidateState.Failed;
            }
        }
    }

    public void MarkRequestSuccess(string profileId)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(profileId))
            {
                _states[profileId] = FailoverRelayCandidateState.Healthy;
            }
        }
    }

    public void MarkHealthRecovered(string profileId)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(profileId))
            {
                _states[profileId] = FailoverRelayCandidateState.Recovering;
            }
        }
    }
}
