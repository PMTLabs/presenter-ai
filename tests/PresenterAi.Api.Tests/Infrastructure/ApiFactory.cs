using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Content;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Sessions;
using PresenterAi.Infrastructure.Content;
using Microsoft.IdentityModel.Tokens;

namespace PresenterAi.Api.Tests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public IReadOnlyDictionary<string, string?>? Overrides { get; set; }
    public string EnvironmentName { get; set; } = "Testing";
    public bool UseQueuedPresenter { get; set; }

    public HttpClient CreateAuthenticatedClient(string? userId = null, string? email = null, string role = "user")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", CreateTestToken(userId ?? "test-user", email ?? "test@presenter-ai.local", role));
        return client;
    }

    public static string CreateTestToken(
        string userId,
        string email,
        string role = "user",
        string issuer = "https://test.presenter-ai.local",
        string audience = "presenter-ai-tests",
        string signingKey = "test-only-jwt-secret-key-not-a-credential-123456",
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim("role", role),
                new Claim(ClaimTypes.Role, role)
            ],
            notBefore: notBefore ?? now,
            expires: expires ?? now.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("Upstream:Endpoint", "https://api.openai.com");
        builder.UseSetting("Upstream:Key", "test");
        builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Port=9;Database=presenter_ai_test;Username=test;Password=test");
        builder.UseSetting("ConnectionStrings:Redis", "localhost:9");
        builder.UseSetting("Jwt:Issuer", "https://test.presenter-ai.local");
        builder.UseSetting("Jwt:Audience", "presenter-ai-tests");
        builder.UseSetting("Jwt:SecretKey", "test-only-jwt-secret-key-not-a-credential-123456");
        builder.UseSetting("Jwt:AccessTokenMinutes", "60");
        builder.UseSetting("Jwt:RefreshTokenDays", "30");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:47914");
        builder.UseSetting("Cors:AllowedOrigins:1", "http://localhost:47915");
        builder.UseSetting("Auth:Dev:Enabled", "true");
        builder.UseSetting("Auth:Dev:UserId", "test-user");
        builder.UseSetting("Auth:Dev:Email", "test@presenter-ai.local");
        builder.UseSetting("Auth:Dev:DisplayName", "Test User");
        builder.UseSetting("Content:RootDir", FindRepositoryRoot());
        // No web root by default: a test that wants the SPA served must opt in with WebRootFixture, so nothing
        // passes only because web/app/dist happens to be built on the developer's machine (CI never builds it).
        builder.UseSetting("Content:WebRoot", Path.Combine(Path.GetTempPath(), "presenter-ai-no-web-root"));

        if (Overrides is not null)
        {
            foreach (var setting in Overrides)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }

        builder.ConfigureServices(services =>
        {
            if (UseQueuedPresenter)
            {
                services.RemoveAll<IPresenter>();
                services.AddSingleton<TestQueuedPresenter>();
                services.AddSingleton<IPresenter>(serviceProvider => serviceProvider.GetRequiredService<TestQueuedPresenter>());
            }

            services.RemoveAll<ITicketStore>();
            services.RemoveAll<IPresentationRepository>();
            services.AddScoped<IPresentationRepository, TestPresentationRepository>();
            services.AddSingleton<TestTicketStore>();
            services.AddSingleton<ITicketStore>(serviceProvider => serviceProvider.GetRequiredService<TestTicketStore>());
            services.RemoveAll<ISessionRecorderFactory>();
            services.AddSingleton<TestSessionRecorderFactory>();
            services.AddSingleton<ISessionRecorderFactory>(serviceProvider =>
                serviceProvider.GetRequiredService<TestSessionRecorderFactory>());
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }
}

internal sealed class TestQueuedPresenter : IPresenter
{
    private PresenterSnapshot _snapshot = new("idle", null, null, 0, 0, false, false, null, null, 0, 200);
    private int _starts;
    public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StartGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken StartCancellationToken { get; private set; }

    public event Action<PresenterSnapshot>? State;
    public event Action<int>? Slide { add { } remove { } }
    public event Action<PresenterAudio>? Audio { add { } remove { } }
    public event Action<PresenterTranscript>? Transcript { add { } remove { } }
    public event Action<PresenterUsage>? Usage { add { } remove { } }
    public event Action<PresenterClosed>? Closed;
    public event Action<PresenterLog>? Log { add { } remove { } }
    public event Action<PresenterUpstreamError>? UpstreamError { add { } remove { } }

    public PresenterSnapshot Snapshot() => _snapshot;

    public async Task<PresenterStartResult> StartAsync(string id, int? fromIndex, string ownerId, CancellationToken cancellationToken = default)
    {
        StartCancellationToken = cancellationToken;
        if (Interlocked.Increment(ref _starts) != 1)
        {
            return new PresenterStartResult(false, id, null, null, null);
        }

        StartEntered.TrySetResult();
        await StartGate.Task.ConfigureAwait(false);
        _snapshot = new("presenting", id, id, 0, 1, false, false, "queued", null, 0, 200);
        State?.Invoke(_snapshot);
        return new PresenterStartResult(true, id, "test", "queued", "test-model");
    }

    public Task<bool> EndAsync(CancellationToken cancellationToken = default)
    {
        _snapshot = new("idle", null, null, 0, 0, false, false, null, null, 0, 200);
        Closed?.Invoke(new PresenterClosed("disconnect", 0));
        State?.Invoke(_snapshot);
        return Task.FromResult(true);
    }

    public Task<bool> NextAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> PrevAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> GotoAsync(int index, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> PauseAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> ResumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> MuteAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> UnmuteAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestPresentationRepository : IPresentationRepository
{
    private readonly FilePresentationRepository _source;

    public TestPresentationRepository(IOptions<ContentOptions> options, IWebHostEnvironment environment)
    {
        _source = new FilePresentationRepository(Path.GetFullPath(options.Value.RootDir, environment.ContentRootPath));
    }

    public async Task<PresentationListResult> ListAsync(
        string ownerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(ownerId);
        var rows = await _source.ListFilesAsync(cancellationToken).ConfigureAwait(false);
        var offset = (long)(page - 1) * pageSize;
        var skip = (int)Math.Min(offset, int.MaxValue);
        return new PresentationListResult(rows.Skip(skip).Take(pageSize).ToArray(), rows.Count);
    }

    public Task<LoadedPresentation> LoadAsync(string ownerId, string id, CancellationToken cancellationToken = default)
    {
        EnsureOwner(ownerId);
        return _source.ReadAsync(id, cancellationToken);
    }

    public Task<string?> FindIdBySlugAsync(string ownerId, string slug, CancellationToken cancellationToken = default)
    {
        EnsureOwner(ownerId);
        return Task.FromResult<string?>(slug);
    }

    private static void EnsureOwner(string ownerId)
    {
        if (!string.Equals(ownerId, "test-user", StringComparison.Ordinal))
        {
            throw new FileNotFoundException("The owner-scoped presentation was not found.");
        }
    }
}

internal sealed class TestSessionRecorderFactory : ISessionRecorderFactory
{
    public List<TestSessionRecorder> Recorders { get; } = [];
    public TaskCompletionSource EndGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool GateEnd { get; set; }

    public ISessionRecorder Create()
    {
        var recorder = new TestSessionRecorder(this);
        Recorders.Add(recorder);
        return recorder;
    }
}

internal sealed class TestSessionRecorder(TestSessionRecorderFactory factory) : ISessionRecorder
{
    private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPresenter? _presenter;
    private Action<int>? _slide;
    private Action<PresenterTranscript>? _transcript;
    private Action<PresenterUsage>? _usage;
    private Action<PresenterClosed>? _closed;
    private int _ended;

    public int AttachCount { get; private set; }
    public int DetachCount { get; private set; }
    public int BeginCount { get; private set; }
    public int EndCount { get; private set; }
    public bool BeginBeforeEnd { get; private set; }
    public bool ClosedBeforeEnd { get; private set; }

    public void Attach(IPresenter presenter)
    {
        _presenter = presenter;
        _slide = _ => { };
        _transcript = _ => { };
        _usage = _ => { };
        _closed = _ =>
        {
            if (Volatile.Read(ref _ended) == 0) ClosedBeforeEnd = true;
        };
        presenter.Slide += _slide;
        presenter.Transcript += _transcript;
        presenter.Usage += _usage;
        presenter.Closed += _closed;
        AttachCount++;
    }

    public void Detach()
    {
        if (_presenter is null) return;
        _presenter.Slide -= _slide;
        _presenter.Transcript -= _transcript;
        _presenter.Usage -= _usage;
        _presenter.Closed -= _closed;
        _presenter = null;
        DetachCount++;
    }

    public Task BeginAsync(string userId, PresenterStartResult result, CancellationToken cancellationToken = default)
    {
        BeginCount++;
        BeginBeforeEnd = EndCount == 0;
        return Task.CompletedTask;
    }

    public Task EndAsync(string closeReason = "disconnect", double? seconds = null)
    {
        if (Interlocked.CompareExchange(ref _ended, 1, 0) == 0)
        {
            EndCount++;
            if (!factory.GateEnd) _end.TrySetResult();
            else _ = factory.EndGate.Task.ContinueWith(_ => _end.TrySetResult(), TaskScheduler.Default);
        }

        return _end.Task;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestTicketStore : ITicketStore
{
    private readonly ConcurrentDictionary<string, (string UserId, DateTimeOffset ExpiresAt)> _tickets = new();

    public Task IssueAsync(string ticketId, string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tickets[ticketId] = (userId, DateTimeOffset.UtcNow.AddMinutes(1));
        return Task.CompletedTask;
    }

    public Task<string?> ClaimAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_tickets.TryRemove(ticketId, out var ticket) || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(ticket.UserId);
    }

    public void Expire(string ticketId)
    {
        if (_tickets.TryGetValue(ticketId, out var ticket))
            _tickets[ticketId] = (ticket.UserId, DateTimeOffset.UtcNow.AddSeconds(-1));
    }
}
