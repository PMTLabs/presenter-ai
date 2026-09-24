using System.Collections.Concurrent;

namespace PresenterAi.Application.Scripts.Revisions;

/// <summary>
/// Process-local <see cref="IPresentationRevisionStore"/> with the same CAS semantics as the Postgres store: writers of
/// one presentation are serialised (like the row lock taken by the version <c>UPDATE</c>), a stale expected version
/// conflicts and writes nothing, and readers see only committed state. Used by file mode, API tests and Application
/// tests (singleton). A presentation is seeded as revision 1 on first use from the seed loader, or explicitly with
/// <see cref="Seed"/>; it belongs to the owner it was first seen with.
/// </summary>
public sealed class InMemoryPresentationRevisionStore : IPresentationRevisionStore
{
    public const string SeedSummary = "Initial version";

    private readonly Func<string, string, CancellationToken, Task<string?>>? _seedLoader;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _seedGate = new(1, 1);

    /// <param name="seedLoader">
    /// Returns the Markdown of a presentation not yet held (ownerId, presentationId), or null when it does not exist.
    /// </param>
    /// <param name="timeProvider">Stamps <see cref="RevisionInfo.CreatedAt"/>; defaults to the system clock.</param>
    public InMemoryPresentationRevisionStore(
        Func<string, string, CancellationToken, Task<string?>>? seedLoader = null,
        TimeProvider? timeProvider = null)
    {
        _seedLoader = seedLoader;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Test seam: awaited inside <see cref="TryAppendAsync"/> after the version check succeeded and before anything is
    /// written (between CAS and <c>COMMIT</c>). Receives the presentation id and the append's token.
    /// </summary>
    public Func<string, CancellationToken, Task>? BeforeCommitAsync { get; set; }

    /// <summary>Seeds (or replaces) a presentation as revision 1 with <paramref name="markdown"/>.</summary>
    public void Seed(string ownerId, string presentationId, string markdown)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);
        ArgumentNullException.ThrowIfNull(markdown);
        _entries[presentationId] = CreateEntry(ownerId, presentationId, markdown);
    }

    public async Task<PresentationHead?> GetHeadAsync(
        string ownerId,
        string presentationId,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(ownerId, presentationId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        lock (entry.Sync)
        {
            return entry.Head;
        }
    }

    public async Task<RevisionList?> ListAsync(
        string ownerId,
        string presentationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var entry = await GetEntryAsync(ownerId, presentationId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        lock (entry.Sync)
        {
            var items = entry.Revisions
                .OrderByDescending(r => r.Number)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(r => r.Info)
                .ToArray();
            return new RevisionList(items, entry.Revisions.Count, entry.Head.Version);
        }
    }

    public async Task<RevisionRecord?> GetAsync(
        string ownerId,
        string presentationId,
        int number,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(ownerId, presentationId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        lock (entry.Sync)
        {
            return entry.Revisions.FirstOrDefault(r => r.Number == number);
        }
    }

    public async Task<AppendResult> TryAppendAsync(
        string ownerId,
        string presentationId,
        int expectedVersion,
        NewRevision revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (!RevisionSources.IsKnown(revision.Source))
        {
            throw new ArgumentException($"Unknown revision source '{revision.Source}'.", nameof(revision));
        }

        var entry = await GetEntryAsync(ownerId, presentationId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return new AppendResult.NotFound();
        }

        await entry.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int current;
            lock (entry.Sync)
            {
                current = entry.Head.Version;
            }

            if (current != expectedVersion)
            {
                return new AppendResult.Conflict(current);
            }

            var script = ScriptParser.Parse(revision.Script, presentationId);
            if (BeforeCommitAsync is { } beforeCommit)
            {
                await beforeCommit(presentationId, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var number = expectedVersion + 1;
            var info = new RevisionInfo(
                number,
                revision.Source,
                _timeProvider.GetUtcNow(),
                revision.Summary,
                revision.BaseVersion,
                revision.RevertedFrom,
                revision.ChangedSlides.ToArray(),
                revision.CreatedBy);
            lock (entry.Sync)
            {
                entry.Revisions.Add(new RevisionRecord(info, revision.Script));
                entry.Head = new PresentationHead(presentationId, number, revision.Script, script);
            }

            return new AppendResult.Applied(number);
        }
        finally
        {
            entry.WriteLock.Release();
        }
    }

    private async Task<Entry?> GetEntryAsync(string ownerId, string presentationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);
        if (!_entries.TryGetValue(presentationId, out var entry) && _seedLoader is not null)
        {
            await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_entries.TryGetValue(presentationId, out entry))
                {
                    var markdown = await _seedLoader(ownerId, presentationId, cancellationToken).ConfigureAwait(false);
                    if (markdown is not null)
                    {
                        entry = CreateEntry(ownerId, presentationId, markdown);
                        _entries[presentationId] = entry;
                    }
                }
            }
            finally
            {
                _seedGate.Release();
            }
        }

        return entry is not null && string.Equals(entry.OwnerId, ownerId, StringComparison.Ordinal) ? entry : null;
    }

    private Entry CreateEntry(string ownerId, string presentationId, string markdown)
    {
        var script = ScriptParser.Parse(markdown, presentationId);
        var info = new RevisionInfo(1, RevisionSources.Import, _timeProvider.GetUtcNow(), SeedSummary, null, null, [], null);
        return new Entry(ownerId, new PresentationHead(presentationId, 1, markdown, script), new RevisionRecord(info, markdown));
    }

    private sealed class Entry(string ownerId, PresentationHead head, RevisionRecord first)
    {
        public string OwnerId { get; } = ownerId;

        public object Sync { get; } = new();

        /// <summary>Serialises writers of this presentation, like the row lock of the version UPDATE.</summary>
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public PresentationHead Head { get; set; } = head;

        public List<RevisionRecord> Revisions { get; } = [first];
    }
}
