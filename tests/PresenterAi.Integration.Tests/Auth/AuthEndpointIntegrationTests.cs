using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Auth;
using PresenterAi.Application.Auth;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Auth;

[Collection(IntegrationCollection.Name)]
public sealed class AuthEndpointIntegrationTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Dev_and_sso_token_routes_are_mapped_and_refuse_disabled_users()
    {
        using var factory = new IntegrationApiFactory(postgres, redis);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await using (var setup = new PresenterAiDbContext(new DbContextOptionsBuilder<PresenterAiDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options))
        {
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync("TRUNCATE TABLE session_turns, sessions, presentations, refresh_tokens, external_logins, users CASCADE");
        }

        var devResponse = await client.PostAsync("/v1/auth/dev/sign-in", content: null);
        devResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var devUserId = JsonNode.Parse(await devResponse.Content.ReadAsStringAsync())!["user"]!["id"]!.GetValue<string>();
        await DisableUserAsync(postgres.ConnectionString, devUserId);

        var disabledDevResponse = await client.PostAsync("/v1/auth/dev/sign-in", content: null);
        disabledDevResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (JsonNode.Parse(await disabledDevResponse.Content.ReadAsStringAsync())!["code"]!.GetValue<string>())
            .Should().Be("auth.account_disabled");

        var ssoUser = new User
        {
            Email = "disabled-sso@example.test",
            AuthMethod = "google",
            IsDisabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await using (var setup = new PresenterAiDbContext(new DbContextOptionsBuilder<PresenterAiDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options))
        {
            setup.Users.Add(ssoUser);
            await setup.SaveChangesAsync();
        }

        const string verifier = "3xY7pQfL2mNvR8sT1uW4zA6bC9dE0gH5jK7lM2nP4qS";
        var rawCode = "disabled-sso-code";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var codes = scope.ServiceProvider.GetRequiredService<ISsoCodeStore>();
            await codes.IssueAsync(TokenService.Hash(rawCode),
                new SsoCode(ssoUser.Id, Challenge(verifier), "https://app.example.test/callback"));
        }

        var tokenResponse = await client.PostAsync("/v1/auth/sso/token",
            new StringContent($"{{\"code\":\"{rawCode}\",\"codeVerifier\":\"{verifier}\"}}", Encoding.UTF8, "application/json"));
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync())!["code"]!.GetValue<string>())
            .Should().Be("auth.account_disabled");
    }

    [Fact]
    public async Task Current_user_and_session_ticket_routes_serve_the_signed_in_user_and_refuse_disabled_accounts()
    {
        using var factory = new IntegrationApiFactory(postgres, redis);
        using var client = factory.CreateClient();

        await using (var setup = new PresenterAiDbContext(new DbContextOptionsBuilder<PresenterAiDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options))
        {
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync("TRUNCATE TABLE session_turns, sessions, presentations, refresh_tokens, external_logins, users CASCADE");
        }

        var anonymousTicket = await client.PostAsync("/v1/sessions/ticket", content: null);
        anonymousTicket.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeAsync(anonymousTicket)).Should().Be("auth.required");

        var signIn = JsonNode.Parse(await (await client.PostAsync("/v1/auth/dev/sign-in", content: null)).Content.ReadAsStringAsync())!;
        var userId = signIn["user"]!["id"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", signIn["accessToken"]!.GetValue<string>());

        var me = await client.GetAsync("/v1/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var meBody = JsonNode.Parse(await me.Content.ReadAsStringAsync())!;
        meBody["id"]!.GetValue<string>().Should().Be(userId);
        meBody["email"]!.GetValue<string>().Should().Be("integration-dev@example.test");

        var ticketResponse = await client.PostAsync("/v1/sessions/ticket", content: null);
        ticketResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var ticketBody = JsonNode.Parse(await ticketResponse.Content.ReadAsStringAsync())!;
        ticketBody["expiresInSeconds"]!.GetValue<int>().Should().Be(30);
        var ticket = ticketBody["ticket"]!.GetValue<string>();
        var tickets = factory.Services.GetRequiredService<ITicketStore>();
        (await tickets.ClaimAsync(ticket)).Should().Be(userId);
        (await tickets.ClaimAsync(ticket)).Should().BeNull();

        await DisableUserAsync(postgres.ConnectionString, userId);

        var disabledMe = await client.GetAsync("/v1/auth/me");
        disabledMe.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeAsync(disabledMe)).Should().Be("auth.account_disabled");
        var disabledTicket = await client.PostAsync("/v1/sessions/ticket", content: null);
        disabledTicket.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeAsync(disabledTicket)).Should().Be("auth.account_disabled");
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["code"]!.GetValue<string>();

    private static async Task DisableUserAsync(string connectionString, string userId)
    {
        await using var db = new PresenterAiDbContext(new DbContextOptionsBuilder<PresenterAiDbContext>()
            .UseNpgsql(connectionString).Options);
        var user = await db.Users.SingleAsync(candidate => candidate.Id == userId);
        user.IsDisabled = true;
        await db.SaveChangesAsync();
    }

    private static string Challenge(string verifier) => Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
