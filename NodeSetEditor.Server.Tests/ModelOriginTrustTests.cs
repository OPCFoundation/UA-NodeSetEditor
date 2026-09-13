using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Guards the provenance boundary around dependency resolution.
///
/// A model row is keyed by namespace URI, and dependency resolution looks rows up by URI across
/// the whole DB. Before <see cref="ModelOrigin"/> existed, "shared" was inferred from link flags
/// (published OR reachable through any non-private workspace link OR orphaned), so a model one
/// user authored or uploaded under a published namespace became the authoritative answer for that
/// URI for EVERY other user — their imports silently resolved the dependency to the local row and
/// the UA Cloud Library was never consulted. That is exactly how a hand-built copy of
/// http://opcfoundation.org/UA/Mining/General/ ended up standing in for the published nodeset in
/// three unrelated workspaces.
///
/// The rule these tests pin down: only a CloudLibrary-origin row (or, as a last resort, an
/// explicitly published one) may satisfy ANOTHER workspace's lookup. Authoring and uploading in
/// your own workspace stay unrestricted.
/// </summary>
[Collection("Api")]
public class ModelOriginTrustTests : UaRestTestBase
{
    public ModelOriginTrustTests(ApiFixture fixture) : base(fixture) { }

    private const string OwnerId = "origin-owner-001", OwnerEmail = "origin-owner@test.net";
    private const string OtherId = "origin-other-001", OtherEmail = "origin-other@test.net";
    private const string OwnerWsName = "OriginOwnerWorkspace";
    private const string OtherWsName = "OriginOtherWorkspace";

    // The namespace the "squatted" row claims, and the one that depends on it.
    private const string SquattedUri = "http://test.example.org/UA/OriginSquatted/";
    private const string DependentUri = "http://test.example.org/UA/OriginDependent/";

    /// <summary>
    /// A model another user authored is never used to satisfy this workspace's dependency —
    /// even when it carries a non-private workspace link, which used to be enough to make it
    /// globally authoritative for its URI.
    /// </summary>
    [Fact]
    public async Task ForeignAuthoredModel_DoesNotSatisfyDependency()
    {
        string? ownerUrn = null, otherUrn = null;
        try
        {
            await FindAndDeleteWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            await FindAndDeleteWorkspace(OtherId, OtherEmail, OtherWsName);

            // Owner authors a model for the namespace and it gets a NON-PRIVATE link — the exact
            // DB shape that used to make it everyone's answer for this URI.
            ownerUrn = await CreateWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            var squattedId = await CreateModel(OwnerId, OwnerEmail, ownerUrn, SquattedUri, "OriginSquatted");
            await MakeLinkNonPrivate(squattedId);
            Assert.Equal(ModelOrigin.Authored, await GetOrigin(squattedId));

            // A different user imports a nodeset that REQUIRES that namespace. The Cloud Library
            // has no such namespace (it is a test URI), so the dependency must simply go
            // unresolved — it must NOT bind to the owner's authored row.
            otherUrn = await CreateWorkspace(OtherId, OtherEmail, OtherWsName);
            await ImportNodeSet(OtherId, OtherEmail, otherUrn, BuildDependentNodeSet());

            var models = await ListModels(OtherId, OtherEmail, otherUrn);
            var uris = models.GetProperty("results").EnumerateArray()
                .Select(m => m.GetProperty("uri").GetString())
                .ToList();

            Assert.Contains(DependentUri, uris);
            Assert.DoesNotContain(SquattedUri, uris);

            // And the row itself must not have been pulled into the other user's workspace.
            using var scope = Fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
            var leaked = await db.WorkspaceModels
                .AnyAsync(wm => wm.ModelId == Guid.Parse(squattedId)
                    && wm.Workspace!.Name == OtherWsName);
            Assert.False(leaked, "the other user's workspace linked the authored model");
        }
        finally
        {
            if (otherUrn != null) await DeleteWorkspace(OtherId, OtherEmail, otherUrn);
            if (ownerUrn != null) await DeleteWorkspace(OwnerId, OwnerEmail, ownerUrn);
            await PurgeModels(SquattedUri, DependentUri);
        }
    }

    /// <summary>
    /// Publishing is what would hand a local edit to every other user, so it is refused for a
    /// namespace the UA Cloud Library already publishes. Editing it in the owning workspace is
    /// unaffected — only the hand-off is closed.
    /// </summary>
    [Fact]
    public async Task Publish_IsBlocked_ForCloudLibraryNamespace()
    {
        // Seeded directly: the create endpoint already refuses opcfoundation.org/UA/ URIs, so the
        // only way to reach the publish guard with such a namespace is a row that predates it.
        const string cloudUri = "http://opcfoundation.org/UA/Machinery/";
        string? ownerUrn = null;
        Guid modelId = Guid.Empty;
        try
        {
            await FindAndDeleteWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            ownerUrn = await CreateWorkspace(OwnerId, OwnerEmail, OwnerWsName);

            using (var scope = Fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
                var ws = await db.Workspaces.FirstAsync(w => w.Name == OwnerWsName);

                // A pre-existing Machinery row may already be in the shared test DB (imported as a
                // dependency by another test) — reuse it rather than colliding on (Uri, VersionNorm).
                var model = await db.Models.FirstOrDefaultAsync(m => m.Uri == cloudUri);
                if (model == null)
                {
                    model = new NodeSetEditor.Model.Model
                    {
                        Uri = cloudUri,
                        Name = "Machinery (seeded)",
                        Version = "1.0.0-alpha",
                        Origin = ModelOrigin.Authored,
                        License = "MIT",
                        CopyrightHolder = "Test Copyright Holder",
                    };
                    model.SetVersionNorm("1.0.0-alpha");
                    db.Models.Add(model);
                    await db.SaveChangesAsync();
                }
                modelId = model.Id;

                if (!await db.WorkspaceModels.AnyAsync(wm => wm.WorkspaceId == ws.Id && wm.ModelId == modelId))
                {
                    db.WorkspaceModels.Add(new WorkspaceModel
                    {
                        WorkspaceId = ws.Id,
                        ModelId = modelId,
                        IsPrivate = true,
                        IsEditable = true,
                    });
                    await db.SaveChangesAsync();
                }
            }

            var resp = await Checkin(OwnerId, OwnerEmail, ownerUrn!, modelId.ToString(),
                "publish", "Attempt to publish a Cloud Library namespace");
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("Cloud Library", body);

            using (var scope = Fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
                var after = await db.Models.AsNoTracking().FirstAsync(m => m.Id == modelId);
                Assert.False(after.Published, "the model was published despite the guard");
            }
        }
        finally
        {
            if (ownerUrn != null) await DeleteWorkspace(OwnerId, OwnerEmail, ownerUrn);
        }
    }

    /// <summary>Minimal NodeSet XML declaring a RequiredModel on <see cref="SquattedUri"/>.</summary>
    private static string BuildDependentNodeSet() => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
          <NamespaceUris>
            <Uri>{DependentUri}</Uri>
            <Uri>{SquattedUri}</Uri>
          </NamespaceUris>
          <Models>
            <Model ModelUri="{DependentUri}" Version="1.0.0" ModelVersion="1.0.0" PublicationDate="2026-01-01T00:00:00Z">
              <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.04" PublicationDate="2024-12-01T00:00:00Z" />
              <RequiredModel ModelUri="{SquattedUri}" Version="1.0.0" PublicationDate="2026-01-01T00:00:00Z" />
            </Model>
          </Models>
          <UAObjectType NodeId="ns=1;i=1001" BrowseName="1:OriginDependentType">
            <DisplayName>OriginDependentType</DisplayName>
            <References>
              <Reference ReferenceType="i=45" IsForward="false">i=58</Reference>
            </References>
          </UAObjectType>
        </UANodeSet>
        """;

    private async Task<ModelOrigin> GetOrigin(string modelId)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        var id = Guid.Parse(modelId);
        return (await db.Models.AsNoTracking().FirstAsync(m => m.Id == id)).Origin;
    }

    /// <summary>
    /// Flip the model's workspace link to non-private, reproducing the DB state that used to make
    /// a model globally authoritative for its URI. There is deliberately no API for this any more.
    /// </summary>
    private async Task MakeLinkNonPrivate(string modelId)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        var id = Guid.Parse(modelId);
        await db.WorkspaceModels.Where(wm => wm.ModelId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(wm => wm.IsPrivate, false));
    }

    private async Task ImportNodeSet(string userId, string email, string wsUrn, string xml)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info/import", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(xml)), "file", "dependent.NodeSet2.xml" },
            { new StringContent("dependent.NodeSet2.xml"), "fileName" },
            { new StringContent("0"), "chunkIndex" },
            { new StringContent("1"), "totalChunks" },
            { new StringContent("MIT"), "license" },
            { new StringContent("Test Copyright Holder"), "copyrightHolder" },
        };
        req.Content = form;
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"import failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<HttpResponseMessage> Checkin(
        string userId, string email, string wsUrn, string modelId, string action, string description)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new { action, description });
        return await Client.SendAsync(req);
    }

    private async Task PurgeModels(params string[] uris)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        var ids = await db.Models.Where(m => uris.Contains(m.Uri)).Select(m => m.Id).ToListAsync();
        foreach (var id in ids)
        {
            await db.References.Where(r => r.ModelId == id).ExecuteDeleteAsync();
            await db.Nodes.Where(n => n.ModelId == id).ExecuteDeleteAsync();
            await db.WorkspaceModels.Where(wm => wm.ModelId == id).ExecuteDeleteAsync();
        }
        await db.Models.Where(m => uris.Contains(m.Uri)).ExecuteDeleteAsync();
    }

    private async Task<string> CreateWorkspace(string userId, string email, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
        req.Content = JsonContent.Create(new { applicationName = name, description = "origin-trust test" });
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applicationUri").GetString()!;
    }

    private async Task<string> CreateModel(string userId, string email, string wsUrn, string uri, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new
        {
            uri,
            name,
            version = "1.0.0",
            license = "MIT",
            copyrightHolder = "Test Copyright Holder"
        });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"CreateModel failed ({resp.StatusCode}): {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ListModels(string userId, string email, string wsUrn)
    {
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/namespaces/info", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task DeleteWorkspace(string userId, string email, string wsUrn)
    {
        var req = AsUser(HttpMethod.Delete,
            $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
        await Client.SendAsync(req);
    }

    private async Task FindAndDeleteWorkspace(string userId, string email, string wsName)
    {
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/discovery", userId, email);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");

        var existing = results.EnumerateArray()
            .FirstOrDefault(ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == wsName);
        if (existing.ValueKind == JsonValueKind.Undefined) return;

        await DeleteWorkspace(userId, email, existing.GetProperty("applicationUri").GetString()!);
    }
}
