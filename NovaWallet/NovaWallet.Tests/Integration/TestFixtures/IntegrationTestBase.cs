using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace NovaWallet.Tests.Integration.TestFixtures;

/// <summary>
/// Shared PostgreSQL container for all integration tests in a collection.
/// The container starts once per test run and is disposed at the end.
/// Each test gets a fresh schema via database migration + truncation.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("novawallet_test")
        .WithUsername("test")
        .WithPassword("test_secret")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
        .Build();

    public string ConnectionString { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<PostgresContainerFixture> { }

/// <summary>
/// Base class that every integration test class inherits.
/// Provides a <see cref="WebApplicationFactory{TProgram}"/> wired to the shared Postgres container.
/// Each test gets a clean database (all tables truncated) via <see cref="ResetDatabaseAsync"/>.
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected readonly PostgresContainerFixture Fixture;
    protected WebApplicationFactory<Program> Factory { get; private set; } = default!;
    protected HttpClient Client { get; private set; } = default!;

    protected IntegrationTestBase(PostgresContainerFixture fixture)
    {
        Fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", Fixture.ConnectionString);
            builder.UseSetting("Jwt:SigningKey", "test-signing-key-at-least-32-characters-long!!");
            builder.UseSetting("Jwt:Issuer", "novawallet-mock");
            builder.UseSetting("Jwt:Audience", "novawallet-api");
            builder.UseSetting("Wallet:DailyOutboundLimitKobo", "50000000");
        });

        // Ensure schema is created/migrated once per test class instantiation
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        await db.Database.MigrateAsync();

        Client = Factory.CreateClient();

        await ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    protected async Task ResetDatabaseAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE wallet_transactions, audit_logs, idempotency_records, outbox_messages, wallets
            CASCADE
            """);
    }

    protected async Task<string> GetTokenAsync(string customerId)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/token", new { customerId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.Token;
    }

    protected void AuthorizeAs(string token) =>
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    private sealed record TokenResponse(string Token, DateTime ExpiresAt, string CustomerId);
}
