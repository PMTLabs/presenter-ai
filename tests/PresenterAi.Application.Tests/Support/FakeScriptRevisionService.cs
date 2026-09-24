using PresenterAi.Application.Scripts.Revisions;

namespace PresenterAi.TestSupport;

/// <summary>
/// Scriptable <see cref="IScriptRevisionService"/> for presenter, bridge and endpoint tests (plan 010 T1). Records every
/// call; outcomes and heads are set by the test and read back through <see cref="GetReconciliationSnapshot"/> the same
/// way the real service exposes them (monotonic heads, terminal outcomes written once, <see cref="Changed"/> is a
/// payload-free signal). Compiled into Application.Tests and, as a linked file, Api.Tests.
/// </summary>
public sealed class FakeScriptRevisionService : IScriptRevisionService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HeadSnapshot> _heads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditOutcome> _outcomes = new(StringComparer.Ordinal);
    private readonly List<EnqueuedEdit> _enqueued = [];
    private readonly List<RevertCall> _reverts = [];
    private readonly Dictionary<string, TalkRegistration> _talks = new(StringComparer.Ordinal);
    private readonly List<string> _closed = [];
    private int _nextEdit;

    public event Action<string>? Changed;

    public bool IsAvailable { get; set; } = true;

    /// <summary>Result of <see cref="RevertAsync"/>; defaults to <see cref="RevertResult.PresentationNotFound"/>.</summary>
    public Func<RevertCall, RevertResult> OnRevert { get; set; } = _ => new RevertResult.PresentationNotFound();

    public IReadOnlyList<EnqueuedEdit> Enqueued
    {
        get
        {
            lock (_gate)
            {
                return _enqueued.ToArray();
            }
        }
    }

    public IReadOnlyList<RevertCall> Reverts
    {
        get
        {
            lock (_gate)
            {
                return _reverts.ToArray();
            }
        }
    }

    /// <summary>Registrations by talk id, in the order they were opened.</summary>
    public IReadOnlyDictionary<string, TalkRegistration> Talks
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, TalkRegistration>(_talks, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Every <see cref="CloseTalkAsync"/> call, including repeated and off-loop (ticket) ones.</summary>
    public IReadOnlyList<string> ClosedTalkCalls
    {
        get
        {
            lock (_gate)
            {
                return _closed.ToArray();
            }
        }
    }

    public string Enqueue(string talkId, ScriptEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(talkId);
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var id = $"edit_{++_nextEdit}";
            _enqueued.Add(new EnqueuedEdit(id, talkId, request));
            _outcomes[id] = EditOutcome.Queued(request.TargetSlideIndexes);
            return id;
        }
    }

    public Task<RevertResult> RevertAsync(
        string ownerId,
        string presentationId,
        int number,
        string userId,
        CancellationToken requestAborted = default)
    {
        var call = new RevertCall(ownerId, presentationId, number, userId);
        lock (_gate)
        {
            _reverts.Add(call);
        }

        return Task.FromResult(OnRevert(call));
    }

    public void Observe(HeadSnapshot head)
    {
        ArgumentNullException.ThrowIfNull(head);
        lock (_gate)
        {
            if (!_heads.TryGetValue(head.PresentationId, out var current) || head.Version > current.Version)
            {
                _heads[head.PresentationId] = head;
            }
        }
    }

    public ReconciliationSnapshot GetReconciliationSnapshot(string presentationId, IReadOnlyCollection<string> localEditIds)
    {
        ArgumentNullException.ThrowIfNull(localEditIds);
        lock (_gate)
        {
            var outcomes = new Dictionary<string, EditOutcome>(StringComparer.Ordinal);
            foreach (var id in localEditIds)
            {
                if (_outcomes.TryGetValue(id, out var outcome))
                {
                    outcomes[id] = outcome;
                }
            }

            return new ReconciliationSnapshot(_heads.GetValueOrDefault(presentationId), outcomes);
        }
    }

    public TalkRegistration OpenTalk(string talkId, string ownerId, string presentationId, CancellationToken ticketToken)
    {
        var registration = new TalkRegistration(talkId, ownerId, presentationId, CancellationToken.None);
        lock (_gate)
        {
            _talks[talkId] = registration;
        }

        ticketToken.Register(() => _ = CloseTalkAsync(talkId));
        return registration;
    }

    public Task CloseTalkAsync(string talkId)
    {
        TalkRegistration? registration;
        lock (_gate)
        {
            _closed.Add(talkId);
            registration = _talks.GetValueOrDefault(talkId);
        }

        registration?.MarkClosed();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets an edit's outcome like the real worker would: false (and no change) when the edit already has a terminal
    /// outcome. When <paramref name="head"/> is given it is observed in the same critical section.
    /// </summary>
    public bool SetOutcome(string editId, EditOutcome outcome, HeadSnapshot? head = null)
    {
        lock (_gate)
        {
            if (_outcomes.TryGetValue(editId, out var current) && current.IsTerminal)
            {
                return false;
            }

            if (head is not null && (!_heads.TryGetValue(head.PresentationId, out var known) || head.Version > known.Version))
            {
                _heads[head.PresentationId] = head;
            }

            _outcomes[editId] = outcome;
            return true;
        }
    }

    public void RaiseChanged(string presentationId) => Changed?.Invoke(presentationId);

    public sealed record EnqueuedEdit(string Id, string TalkId, ScriptEditRequest Request);

    public sealed record RevertCall(string OwnerId, string PresentationId, int Number, string UserId);
}
