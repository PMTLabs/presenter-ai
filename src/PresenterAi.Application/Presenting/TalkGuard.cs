namespace PresenterAi.Application.Presenting;

/// <summary>Owns the deadlines for one talk. All methods except the timer callback run on the presenter loop.</summary>
internal sealed class TalkGuard : IDisposable
{
    public const int WarningLead = 60;

    private readonly TimeProvider _clock;
    private readonly Action<long> _elapsed;
    private readonly Action _maxElapsed;
    private readonly int _idleSeconds;
    private readonly int _pauseSeconds;
    private ITimer? _timer;
    private long _generation;
    private long? _idleDeadline;
    private long? _pauseDeadline;
    private bool _maxWarned;
    private bool _idleWarned;

    public TalkGuard(TimeProvider clock, int maxMinutes, int idleSeconds, int pauseSeconds,
        Action<long> elapsed, Action maxElapsed)
    {
        _clock = clock;
        _elapsed = elapsed;
        _maxElapsed = maxElapsed;
        _idleSeconds = idleSeconds;
        _pauseSeconds = pauseSeconds;
        StartedAt = clock.GetUtcNow();
        StartedTimestamp = clock.GetTimestamp();
        MaxDeadline = StartedTimestamp + Ticks(TimeSpan.FromMinutes(maxMinutes));
        Schedule();
    }

    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset MaxEndsAt => StartedAt + TimeSpan.FromSeconds(
        (double)(MaxDeadline - StartedTimestamp) / _clock.TimestampFrequency);
    public long MaxDeadline { get; private set; }
    public long Generation => _generation;
    public bool MaxExpired => _clock.GetTimestamp() >= MaxDeadline;

    public void Tighten(int maxMinutes)
    {
        var proposed = StartedTimestamp + Ticks(TimeSpan.FromMinutes(maxMinutes));
        MaxDeadline = proposed;
        Schedule();
    }

    private long StartedTimestamp { get; set; }

    public void StartPresenting()
    {
        _pauseDeadline = null;
        _idleDeadline = _clock.GetTimestamp() + Ticks(TimeSpan.FromSeconds(_idleSeconds));
        _idleWarned = false;
        Schedule();
    }

    public bool Activity()
    {
        if (_idleDeadline is null) return false;
        var cleared = _idleWarned;
        _idleWarned = false;
        _idleDeadline = _clock.GetTimestamp() + Ticks(TimeSpan.FromSeconds(_idleSeconds));
        Schedule();
        return cleared;
    }

    public void Pause()
    {
        _idleDeadline = null;
        _idleWarned = false;
        _pauseDeadline = _clock.GetTimestamp() + Ticks(TimeSpan.FromSeconds(_pauseSeconds));
        Schedule();
    }

    public void StopPauseGrace()
    {
        _pauseDeadline = null;
        Schedule();
    }

    public GuardDue Due()
    {
        var now = _clock.GetTimestamp();
        var maxWarning = !_maxWarned && now >= MaxDeadline - Ticks(TimeSpan.FromSeconds(WarningLead));
        var idleWarning = _idleDeadline is { } idle && !_idleWarned &&
            now >= idle - Ticks(TimeSpan.FromSeconds(WarningLead));
        if (maxWarning) _maxWarned = true;
        if (idleWarning) _idleWarned = true;
        var due = new GuardDue(now >= MaxDeadline, _idleDeadline is { } d && now >= d,
            _pauseDeadline is { } p && now >= p, maxWarning, idleWarning);
        if (due.Pause) _pauseDeadline = null;
        if (!due.Max && !due.Idle) Schedule();
        return due;
    }

    public void Dispose()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    private long Ticks(TimeSpan duration) => (long)(duration.TotalSeconds * _clock.TimestampFrequency);

    private void Schedule()
    {
        _timer?.Dispose();
        var generation = ++_generation;
        var now = _clock.GetTimestamp();
        var next = MaxDeadline;
        if (!_maxWarned) next = Math.Min(next, MaxDeadline - Ticks(TimeSpan.FromSeconds(WarningLead)));
        if (_idleDeadline is { } idle)
        {
            next = Math.Min(next, idle);
            if (!_idleWarned) next = Math.Min(next, idle - Ticks(TimeSpan.FromSeconds(WarningLead)));
        }
        if (_pauseDeadline is { } pause) next = Math.Min(next, pause);
        var wait = TimeSpan.FromSeconds((double)Math.Max(0, next - now) / _clock.TimestampFrequency);
        _timer = _clock.CreateTimer(_ =>
        {
            if (generation != Volatile.Read(ref _generation)) return;
            if (_clock.GetTimestamp() >= MaxDeadline) _maxElapsed();
            _elapsed(generation);
        }, null, wait, Timeout.InfiniteTimeSpan);
    }
}

internal readonly record struct GuardDue(bool Max, bool Idle, bool Pause, bool MaxWarning, bool IdleWarning);
