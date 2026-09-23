namespace PresenterAi.Application.Presenting;

public sealed class ToolRoundTracker
{
    public enum ResponseFinishedResult
    {
        Ignored,
        ToolRound,
        FinalAnswer,
        Failed
    }

    public sealed class TrackedCall
    {
        public string CallId { get; }
        public string DelegationId { get; }
        public ILiveSession Session { get; }
        public long RunGeneration { get; }
        public bool Submitted { get; set; }

        public TrackedCall(string callId, string delegationId, ILiveSession session, long runGeneration)
        {
            CallId = callId;
            DelegationId = delegationId;
            Session = session;
            RunGeneration = runGeneration;
        }
    }

    private sealed class DelegationState
    {
        public string DelegationId { get; }
        public Dictionary<string, TrackedCall> CurrentRoundCalls { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AllSeenCalls { get; } = new(StringComparer.Ordinal);
        public bool ResponseCompletedReceived { get; set; }
        public bool Continued { get; set; }

        public DelegationState(string delegationId)
        {
            DelegationId = delegationId;
        }

        public bool HasCallsInRound => CurrentRoundCalls.Count > 0;

        public bool AllCallsSubmitted => CurrentRoundCalls.Count > 0 && CurrentRoundCalls.Values.All(c => c.Submitted);

        public void StartNewRound()
        {
            CurrentRoundCalls.Clear();
            ResponseCompletedReceived = false;
            Continued = false;
        }
    }

    private readonly Dictionary<string, DelegationState> _delegations = new(StringComparer.Ordinal);

    public bool HasPendingBackendDelegation => _delegations.Count > 0;

    public void OpenDelegation(string delegationId)
    {
        if (string.IsNullOrEmpty(delegationId))
        {
            return;
        }

        if (!_delegations.ContainsKey(delegationId))
        {
            _delegations[delegationId] = new DelegationState(delegationId);
        }
    }

    public bool IsCallDuplicate(string callId)
    {
        return _delegations.Values.Any(d => d.AllSeenCalls.Contains(callId));
    }

    public bool AddCall(string delegationId, string callId, ILiveSession session, long runGeneration)
    {
        if (string.IsNullOrEmpty(delegationId) || string.IsNullOrEmpty(callId))
        {
            return false;
        }

        if (!_delegations.TryGetValue(delegationId, out var state))
        {
            state = new DelegationState(delegationId);
            _delegations[delegationId] = state;
        }

        if (!state.AllSeenCalls.Add(callId))
        {
            return false; // Duplicate call id
        }

        state.CurrentRoundCalls[callId] = new TrackedCall(callId, delegationId, session, runGeneration);
        return true;
    }

    public bool IsCallPending(string delegationId, string callId)
    {
        return _delegations.TryGetValue(delegationId, out var state)
            && state.CurrentRoundCalls.TryGetValue(callId, out var call)
            && !call.Submitted;
    }

    public void MarkCallSubmitted(string delegationId, string callId)
    {
        if (_delegations.TryGetValue(delegationId, out var state) &&
            state.CurrentRoundCalls.TryGetValue(callId, out var call))
        {
            call.Submitted = true;
        }
    }

    public ResponseFinishedResult OnDelegatedResponseFinished(string delegationId, string type)
    {
        if (!_delegations.TryGetValue(delegationId, out var state))
        {
            return ResponseFinishedResult.Ignored;
        }

        if (type == "response.completed")
        {
            if (state.HasCallsInRound)
            {
                // This is a tool round completion!
                state.ResponseCompletedReceived = true;
                if (state.Continued)
                {
                    state.StartNewRound();
                }

                return ResponseFinishedResult.ToolRound;
            }

            // Current round has no calls -> final answer!
            _delegations.Remove(delegationId);
            return ResponseFinishedResult.FinalAnswer;
        }

        // Error or failure
        _delegations.Remove(delegationId);
        return ResponseFinishedResult.Failed;
    }

    public bool ShouldSendContinueResponses()
    {
        var delegationsWithCalls = _delegations.Values.Where(d => d.HasCallsInRound).ToList();
        if (delegationsWithCalls.Count == 0)
        {
            return false;
        }

        return delegationsWithCalls.All(d => d.AllCallsSubmitted && d.ResponseCompletedReceived && !d.Continued);
    }

    public void OnResponsesContinued()
    {
        foreach (var delegation in _delegations.Values.Where(d => d.HasCallsInRound).ToList())
        {
            delegation.StartNewRound();
        }
    }

    public void CloseAllDelegations()
    {
        _delegations.Clear();
    }

    public void Clear()
    {
        _delegations.Clear();
    }
}
