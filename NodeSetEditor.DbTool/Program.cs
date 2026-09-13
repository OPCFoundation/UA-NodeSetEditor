extern alias JsonNodeSet;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodeSetEditor.Model;
using JsonNodeSet::NodeSetTool;
using Opc.Ua.Export;

// Separate positional args from --key=value options
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var command = positional.ElementAtOrDefault(0) ?? "help";

if (command.Equals("help", StringComparison.OrdinalIgnoreCase))
{
    PrintHelp();
    return;
}

// Environment variables sit between user secrets and the command line so the container can
// supply ConnectionStrings__Postgres the same way the server does, rather than putting the
// credentials on a command line where they show up in shell history and process listings.
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets("a53a05a5-4d1c-4630-9815-e9635e41829d")
    .AddEnvironmentVariables()
    .AddCommandLine(args.Where(a => a.StartsWith("--")).ToArray())
    .Build();

var connectionString = config.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres not configured. Use appsettings.json or --ConnectionStrings:Postgres=...");

var verify = args.Any(a => a.Equals("--Verify", StringComparison.OrdinalIgnoreCase)
    || a.StartsWith("--Verify=", StringComparison.OrdinalIgnoreCase));

switch (command.ToLowerInvariant())
{
    case "migrate":
        await MigrateDatabase(connectionString);
        break;
    case "import":
        var webRoot = config["WebRoot"]
            ?? Path.Combine(Directory.GetCurrentDirectory(), "..", "NodeSetEditor.Server", "wwwroot");
        await ImportSharedModels(connectionString, webRoot, verify);
        break;
    case "import-file":
        var filePath = positional.ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(filePath))
        {
            Console.Error.WriteLine("Usage: import-file <path> [--Verify=true]");
            return;
        }
        await ImportSingleFile(connectionString, filePath, verify);
        break;
    case "status":
        await ShowStatus(connectionString);
        break;
    case "script":
        PrintCreateScript(connectionString);
        break;
    default:
        PrintHelp();
        break;
}

// ─── Commands ───────────────────────────────────────────────────────────────

static void PrintHelp()
{
    Console.WriteLine("""
    NodeSetEditor Database Tool

    Commands:
      migrate                     Create/update database schema
      import --WebRoot=<path>     Import shared models from index.json into database
      import-file <path>          Import a single NodeSet file (XML, JSON, JSON-LD, or .tar.gz)
      status                       Show database statistics
      script                       Print the full EF Core CREATE script for the current model
                                    (no DB connection needed — useful for diffing an additive
                                    patch against a live database before running it)

    Options:
      --ConnectionStrings:Postgres=<connstr>   PostgreSQL connection string
      --WebRoot=<path>                          wwwroot path (default: ../NodeSetEditor.Server/wwwroot)
      --File=<path>                             NodeSet file to import (for import-file)
      --Verify=true                             Round-trip verify: export from DB after import and compare
    """);
}

static async Task MigrateDatabase(string connectionString)
{
    Console.WriteLine("Migrating database...");
    await using var db = CreateContext(connectionString);
    await db.Database.EnsureCreatedAsync();
    Console.WriteLine("Database schema created/updated.");
}

static void PrintCreateScript(string connectionString)
{
    using var db = CreateContext(connectionString);
    Console.WriteLine(db.Database.GenerateCreateScript());
}

static async Task ShowStatus(string connectionString)
{
    await using var db = CreateContext(connectionString);

    if (!await db.Database.CanConnectAsync())
    {
        Console.WriteLine("Cannot connect to database.");
        return;
    }

    var workspaces = await db.Workspaces.CountAsync();
    var models = await db.Models.CountAsync();
    var modelsWithContent = await db.Models.CountAsync(m => m.Content != null);
    var nodes = await db.Nodes.CountAsync();
    var references = await db.References.CountAsync();
    var userPrefs = await db.UserPreferences.CountAsync();

    Console.WriteLine($"Database Status:");
    Console.WriteLine($"  Workspaces:        {workspaces}");
    Console.WriteLine($"  Models:            {models} ({modelsWithContent} with content)");
    Console.WriteLine($"  Nodes:             {nodes}");
    Console.WriteLine($"  References:        {references}");
    Console.WriteLine($"  User Preferences:  {userPrefs}");
}

static async Task ImportSharedModels(string connectionString, string webRoot, bool verify = false)
{
    webRoot = Path.GetFullPath(webRoot);
    Console.WriteLine($"Importing shared models from: {webRoot}");

    var indexPath = Path.Combine(webRoot, "index.json");
    if (!File.Exists(indexPath))
    {
        Console.Error.WriteLine($"index.json not found at: {indexPath}");
        return;
    }

    await using var db = CreateContext(connectionString);
    await db.Database.EnsureCreatedAsync();

    var jsonOpts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    var indexJson = await File.ReadAllTextAsync(indexPath);
    var sharedInfos = JsonSerializer.Deserialize<List<FileModelInfo>>(indexJson, jsonOpts) ?? [];
    Console.WriteLine($"Found {sharedInfos.Count} shared models in index.json");

    var imported = 0;
    var skipped = 0;

    foreach (var info in sharedInfos)
    {
        var uri = info.ModelUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            Console.Error.WriteLine($"  [skip] {info.Name} — no ModelUri");
            skipped++;
            continue;
        }

        // Resolve version: ModelVersion first, fall back to Version field
        var versionStr = info.ModelVersion ?? info.Version;
        var versionNorm = NodeSetEditor.Model.Model.NormalizeVersion(versionStr);

        // Check for existing model with same URI + normalized SemVer
        var existing = await db.Models.FirstOrDefaultAsync(m =>
            m.Uri == uri && m.VersionNorm == versionNorm);

        if (existing != null)
        {
            Console.WriteLine($"  [skip] {info.Name} ({uri}) v{versionStr} — already imported");
            skipped++;
            continue;
        }

        byte[]? newContent = ReadContent(webRoot, info.Path);
        if (newContent == null)
        {
            Console.Error.WriteLine($"  [skip] {info.Name} — no content file");
            skipped++;
            continue;
        }

        try
        {
            using var ms = new MemoryStream(newContent);
            var nodeSet = UANodeSet.Read(ms);

            // StoreNodeSetAsync handles SemVer dedup internally
            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);
            model.Name = info.Name;
            model.Description = info.Description;
            // Catalog seeding loads published releases, so they carry the same standing as a
            // Cloud Library copy — without this a freshly seeded DB would be full of
            // Unknown-origin rows that no workspace is allowed to resolve dependencies against.
            model.Origin = ModelOrigin.CloudLibrary;
            model.Content = newContent;
            model.HasErrors = info.HasErrors;
            StampImportLicense(model, newContent);
            await db.SaveChangesAsync();

            var nodeCount = await db.Nodes.CountAsync(n => n.ModelId == model.Id);
            imported++;
            Console.WriteLine($"  [add] {info.Name} ({uri}) v{versionStr} — {nodeCount} nodes, {newContent.Length / 1024}KB");

            if (verify)
                await VerifyRoundTrip(db, model.Uri!, model.Version, newContent);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [error] {info.Name} ({uri}): {ex.Message}");
            skipped++;
        }
    }

    await db.SaveChangesAsync();
    Console.WriteLine($"\nDone: {imported} imported, {skipped} skipped.");
    Console.WriteLine();
    await ShowStatus(connectionString);
}

static async Task ImportSingleFile(string connectionString, string filePath, bool verify = false)
{
    filePath = Path.GetFullPath(filePath);
    Console.WriteLine($"Importing: {filePath}");

    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"File not found: {filePath}");
        Environment.ExitCode = 1;
        return;
    }

    // Load and parse using NodeSetSerializer (auto-detects XML, JSON, JSON-LD, tar.gz)
    var serializer = new NodeSetSerializer();
    try
    {
        serializer.Load(filePath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to parse NodeSet file: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    var models = serializer.Models;
    if (models.Count == 0)
    {
        Console.Error.WriteLine("No models found in file.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"Found {models.Count} model(s) in file.");

    // Read the raw file content for storage
    var content = await File.ReadAllBytesAsync(filePath);

    // Convert to OPC UA Export format for NodeSetConverter
    UANodeSet nodeSet;
    try
    {
        using var ms = new MemoryStream();
        serializer.SaveXml(ms);
        ms.Seek(0, SeekOrigin.Begin);
        nodeSet = UANodeSet.Read(ms);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to convert to UANodeSet: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    if (nodeSet.Models == null || nodeSet.Models.Length == 0)
    {
        Console.Error.WriteLine("No models in converted NodeSet.");
        Environment.ExitCode = 1;
        return;
    }

    await using var db = CreateContext(connectionString);
    await db.Database.EnsureCreatedAsync();

    var modelTable = nodeSet.Models[0];
    var uri = modelTable.ModelUri;
    var versionStr = modelTable.ModelVersion ?? modelTable.Version;

    var name = DeriveModelName(uri ?? "Unknown");
    Console.WriteLine($"  Model: {name} ({uri}) v{versionStr}");

    // StoreNodeSetAsync handles SemVer dedup (replaces same URI+SemVer)
    var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

    model.Name = name;
    // Same standing as catalog seeding above: this command loads a released NodeSet file
    // straight into the shared catalog, not a user's authored copy.
    model.Origin = ModelOrigin.CloudLibrary;
    model.Content = content;
    // Every model must carry a license/copyright. Parse embedded SPDX headers; for OPC
    // Foundation namespaces (the Core UA NodeSet loaded at DB reset) apply the Foundation
    // defaults. Stamp only when not already set (set-once at genesis).
    StampImportLicense(model, content);
    await db.SaveChangesAsync();

    var nodeCount = await db.Nodes.CountAsync(n => n.ModelId == model.Id);
    var refCount = await db.References.CountAsync(r => r.ModelId == model.Id);
    var hierarchyCount = await db.SubTypeHierarchy
        .Join(db.Nodes.Where(n => n.ModelId == model.Id),
            h => h.SubTypeNodeId, n => n.NodeId, (h, _) => h)
        .CountAsync();

    Console.WriteLine($"  [done] {nodeCount} nodes, {refCount} references, {hierarchyCount} hierarchy entries, {content.Length / 1024}KB content");

    if (verify)
        await VerifyRoundTrip(db, model.Uri!, model.Version, content);
}

static string DeriveModelName(string uri)
{
    // "http://opcfoundation.org/UA/DI/" → "DI"
    // "urn:opcfoundation.org:2024-01:DemoModel" → "DemoModel"
    var trimmed = uri.TrimEnd('/');
    var lastSlash = trimmed.LastIndexOf('/');
    var lastColon = trimmed.LastIndexOf(':');
    var sep = Math.Max(lastSlash, lastColon);
    var name =  sep >= 0 ? trimmed[(sep + 1)..] : trimmed;

    if (uri == "http://opcfoundation.org/UA/")
    {
        return "Core";
    }

    return name;
}

// Stamp license/copyright onto a freshly-imported model (set-once at genesis). Resolves from the
// file's embedded SPDX headers, falling back to the OPC Foundation defaults for opcfoundation.org
// namespaces (covers the Core UA NodeSet imported at DB reset). Existing values are preserved.
static void StampImportLicense(Model model, byte[] content)
{
    if (!string.IsNullOrWhiteSpace(model.License)) return;
    var (license, licenseUrl, copyright) = SpdxHeaders.ResolveForImport(content, model.Uri);
    if (string.IsNullOrWhiteSpace(license)) return;
    model.License = license;
    model.LicenseUrl = licenseUrl;
    model.CopyrightHolder = copyright;
}

// ─── Round-trip Verification ─────────────────────────────────────────────────

static async Task VerifyRoundTrip(NodeSetEditorDbContext db, string modelUri, string? version, byte[] originalContent)
{
    Console.Write($"  [verify] Round-trip comparison... ");

    try
    {
        // Load original into a NodeSetSerializer
        var source = new NodeSetSerializer();
        using (var ms = new MemoryStream(originalContent))
        {
            source.LoadXml(ms);
        }

        // Reconstruct from DB and serialize to XML
        var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, modelUri, version);
        byte[] exportedBytes;
        using (var ms = new MemoryStream())
        {
            reconstructed.Write(ms);
            exportedBytes = ms.ToArray();
        }

        // Load exported XML into another NodeSetSerializer
        var exported = new NodeSetSerializer();
        using (var ms = new MemoryStream(exportedBytes))
        {
            exported.LoadXml(ms);
        }

        // Semantic comparison
        if (source.Compare(exported))
        {
            Console.WriteLine("PASS");
        }
        else
        {
            Console.WriteLine("FAIL");
            foreach (var error in source.CompareErrors)
            {
                Console.Error.WriteLine($"    {error}");
            }

            // Write exported file for debugging
            var tempPath = Path.Combine(Path.GetTempPath(), $"verify_{SanitizeFileName(modelUri)}_{version ?? "latest"}.xml");
            await File.WriteAllBytesAsync(tempPath, exportedBytes);
            Console.Error.WriteLine($"    Exported to: {tempPath}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
    }
}

static string SanitizeFileName(string name)
{
    foreach (var c in Path.GetInvalidFileNameChars())
        name = name.Replace(c, '_');
    return name;
}

// ─── Helpers ────────────────────────────────────────────────────────────────

static byte[]? ReadContent(string webRoot, string? relativePath)
{
    if (relativePath == null) return null;
    var xmlPath = Path.Combine(webRoot, "nodesets", relativePath);
    if (File.Exists(xmlPath))
        return File.ReadAllBytes(xmlPath);
    Console.Error.WriteLine($"  [warn] XML file not found: {xmlPath}");
    return null;
}

static NodeSetEditorDbContext CreateContext(string connectionString)
{
    var options = new DbContextOptionsBuilder<NodeSetEditorDbContext>()
        .UseNpgsql(connectionString)
        .Options;
    return new NodeSetEditorDbContext(options);
}

// ─── File-system DTOs (matches existing JSON format) ────────────────────────

class FileModelInfo
{
    public Guid? Id { get; set; }
    public string? ModelUri { get; set; }
    public string? PublicationDate { get; set; }
    public string? ModelVersion { get; set; }
    public string? Version { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Description { get; set; }
    public bool HasErrors { get; set; }
}
