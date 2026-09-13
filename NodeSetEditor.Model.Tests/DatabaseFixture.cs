using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    public class DatabaseFixture : IAsyncLifetime
    {
        public NodeSetEditorDbContext DbContext { get; private set; } = null!;
        public string ConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.test.json")
                .AddUserSecrets<DatabaseFixture>(optional: true)
                .Build();

            ConnectionString = configuration.GetConnectionString("NodeSetEditor")
                ?? throw new InvalidOperationException("ConnectionStrings:NodeSetEditor not found in appsettings.test.json");

            var options = new DbContextOptionsBuilder<NodeSetEditorDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;

            DbContext = new NodeSetEditorDbContext(options);

            // Tests run against an EXISTING schema (provisioned out-of-band by
            // db/initialize_db.ps1). The fixture only clears data; it does NOT
            // drop or create tables — that would invalidate the runtime app role's
            // grants on every test run. Truncate in dependency order so FKs are
            // satisfied without disabling constraints.
            await TruncateAllAsync();
        }

        /// <summary>
        /// Wipes data from every test-relevant table while preserving the schema
        /// and the grants applied to it. CASCADE handles cross-table FK chains.
        /// </summary>
        private async Task TruncateAllAsync()
        {
            // Order is irrelevant with CASCADE; using one statement keeps the
            // operation atomic. Quoted to match EF's PascalCase identifiers.
            await DbContext.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE " +
                "\"TypeDependencies\", " +
                "\"NodeSetTypes\", " +
                "\"SubTypeHierarchy\", " +
                "\"References\", " +
                "\"Nodes\", " +
                "\"WorkspaceModels\", " +
                "\"Models\", " +
                "\"WorkspaceAcls\", " +
                "\"Workspaces\", " +
                "\"UserPreferences\" " +
                "RESTART IDENTITY CASCADE;");
        }

        public async Task DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }

        public NodeSetEditorDbContext CreateNewContext()
        {
            var options = new DbContextOptionsBuilder<NodeSetEditorDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;

            return new NodeSetEditorDbContext(options);
        }
    }

    [CollectionDefinition("Database")]
    public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
    {
    }
}
