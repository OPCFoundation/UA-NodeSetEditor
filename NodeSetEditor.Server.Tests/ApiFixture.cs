using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Model;
using NodeSetEditor.Server.Services;
using Opc.Ua.RestfulApi;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Shared fixture that boots the NodeSetEditor server in-process for integration tests.
/// Uses PostgreSQL DB storage. Injects a fake authenticated user so API calls pass auth checks.
/// </summary>
public class ApiFixture : IAsyncLifetime
{
    public const string TestUserId = "test-user-001";
    public const string TestUserEmail = "test@example.com";

    /// <summary>Shared secret for the validation worker (M2M) endpoints in tests.</summary>
    public const string WorkerApiKey = "test-worker-key-abc123";

    private WebApplicationFactory<Program>? _factory;
    public HttpClient Client { get; private set; } = null!;

    public const string TestModelUri = "http://test.example.org/UA/";

    /// <summary>The workspace URN for tests that need a pre-created workspace.</summary>
    public string? WorkspaceUrn { get; private set; }

    private string? _connectionString;

    public async Task InitializeAsync()
    {
        // Load test connection string
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.test.json")
            .AddUserSecrets<ApiFixture>(optional: true)
            .Build();

        _connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres not found in appsettings.test.json");

        // Ensure database schema exists (but preserve data for workspace reuse)
        var options = new DbContextOptionsBuilder<NodeSetEditorDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        await using (var db = new NodeSetEditorDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        // Program.cs reads ConnectionStrings:Postgres at top-level before the WebApplicationBuilder
        // is built, so WithWebHostBuilder/ConfigureAppConfiguration arrives too late. Environment
        // variables ARE loaded during WebApplication.CreateBuilder, so seed them here. We override
        // only the test-specific values; CloudLibrary* and other shared config flow from Server's
        // user-secrets via the Development environment below.
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _connectionString);
        Environment.SetEnvironmentVariable("DevAuth__Enabled", "true");
        Environment.SetEnvironmentVariable("DevAuth__UserId", TestUserId);
        Environment.SetEnvironmentVariable("DevAuth__Email", TestUserEmail);
        Environment.SetEnvironmentVariable("DevAuth__DisplayName", "Test User");
        Environment.SetEnvironmentVariable("Validation__WorkerApiKey", WorkerApiKey);

        // Use Development so Server's user-secrets (Postgres + CloudLibrary*) are loaded.
        // Env-var overrides above win over user-secrets for the test-specific keys.
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
            });

        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Dev-Auth", "true");

        // Try to create the workspace; if it already exists, reuse it and clean its models
        const string workspaceName = "IntegrationTestWorkspace";
        var createResponse = await Client.PostAsJsonAsync("/api/opcua/v1/servers", new
        {
            applicationName = workspaceName,
            description = "Auto-created for tests"
        });

        if (createResponse.IsSuccessStatusCode)
        {
            var body = await createResponse.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            WorkspaceUrn = body?["applicationUri"]?.ToString();
        }
        else
        {
            // Workspace already exists — look it up via the server's own DbContext
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
            var existing = await db.Workspaces
                .FirstAsync(w => w.Name == workspaceName);
            WorkspaceUrn = UrnUtils.ToUrn(existing.Id);

            // Delete all associated models
            var nsListRequest = new HttpRequestMessage(HttpMethod.Get, "/api/opcua/v1/namespaces/info");
            nsListRequest.Headers.Add("OpcUa-Server", WorkspaceUrn);
            var nsListResponse = await Client.SendAsync(nsListRequest);
            nsListResponse.EnsureSuccessStatusCode();

            var nsBody = await nsListResponse.Content.ReadFromJsonAsync<JsonElement>();
            if (nsBody.TryGetProperty("results", out var nsResults))
            {
                foreach (var ns in nsResults.EnumerateArray())
                {
                    if (ns.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null)
                    {
                        var delRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{idProp.GetString()}");
                        delRequest.Headers.Add("OpcUa-Server", WorkspaceUrn);
                        await Client.SendAsync(delRequest);
                    }
                }
            }
        }

        // Create a private namespace model for CRUD tests
        var nsRequest = new HttpRequestMessage(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
        nsRequest.Headers.Add("OpcUa-Server", WorkspaceUrn);
        nsRequest.Content = JsonContent.Create(new
        {
            uri = TestModelUri,
            name = "TestModel",
            version = "1.0.0",
            license = "MIT",
            copyrightHolder = "Test Copyright Holder"
        });
        var nsResponse = await Client.SendAsync(nsRequest);
        nsResponse.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A client with no DevAuth header, so requests hit the real authorization
    /// pipeline as a truly anonymous caller. Used to assert the fail-closed
    /// fallback policy (and the SPA's anonymous exemptions).
    /// </summary>
    public HttpClient CreateAnonymousClient() => _factory!.CreateClient();

    /// <summary>The server's service provider, for tests that need direct DB cleanup.</summary>
    public IServiceProvider Services => _factory!.Services;

    /// <summary>
    /// A client whose host has <see cref="BetaTesterPolicy"/> built from the supplied
    /// <c>BetaTesterDomains</c> value instead of the ambient configuration. The policy is swapped
    /// in the DI container rather than through an environment variable, so this cannot disturb the
    /// shared host or any test running beside it.
    /// </summary>
    public HttpClient CreateClientWithBetaTesters(string? betaTesterDomains)
    {
        var policy = new BetaTesterPolicy(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BetaTesterPolicy.ConfigurationKey] = betaTesterDomains
            })
            .Build(),
            // These tests exercise the allow-list itself; the test-mode bypass is off.
            new TestModeOptions());

        var client = _factory!
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(policy)))
            .CreateClient();

        client.DefaultRequestHeaders.Add("X-Dev-Auth", "true");
        return client;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();

        if (_factory != null)
            await _factory.DisposeAsync();

        // Clear Npgsql connection pool to release all DB connections
        if (_connectionString != null)
            Npgsql.NpgsqlConnection.ClearAllPools();
    }
}

[CollectionDefinition("Api")]
public class ApiCollection : ICollectionFixture<ApiFixture> { }
