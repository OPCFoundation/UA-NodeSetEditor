using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// NamespaceMetadataType declares ModelVersion as SemanticVersionString (i=24263), but older
/// NodeSets — and models this editor created before that was fixed — carry it as String.
///
/// An imported NodeSet is stored exactly as authored, though: uploading must not rewrite it,
/// even when the upload is an editable private copy. The property is corrected only when
/// something changes the model's version, such as Finish Editing (check-in "keep").
/// </summary>
[Collection("Api")]
public class NamespaceMetadataSyncScopeTests : UaRestTestBase
{
    private const string ModelUri = "http://test.example.org/UA/NsMetaSyncScope/";
    private const string ModelVersionNodeId = "nsu=" + ModelUri + ";i=6001";
    private const string SemanticVersionString = "i=24263";
    private const string StringDataType = "i=12";

    public NamespaceMetadataSyncScopeTests(ApiFixture fixture) : base(fixture) { }

    // A NamespaceMetadata object whose ModelVersion property is typed String, as older
    // NodeSets ship it.
    private const string NodeSetXml = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
          <NamespaceUris><Uri>{ModelUri}</Uri></NamespaceUris>
          <Models>
            <Model ModelUri="{ModelUri}" Version="1.0.0" ModelVersion="1.0.0" PublicationDate="2026-01-01T00:00:00Z">
              <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.03" PublicationDate="2023-12-15T00:00:00Z" />
            </Model>
          </Models>
          <Aliases>
            <Alias Alias="String">i=12</Alias>
            <Alias Alias="HasComponent">i=47</Alias>
            <Alias Alias="HasProperty">i=46</Alias>
            <Alias Alias="HasTypeDefinition">i=40</Alias>
          </Aliases>
          <UAObject NodeId="ns=1;i=5001" BrowseName="1:{ModelUri}">
            <DisplayName>{ModelUri}</DisplayName>
            <References>
              <Reference ReferenceType="HasTypeDefinition">i=11616</Reference>
              <Reference ReferenceType="HasComponent" IsForward="false">i=11715</Reference>
              <Reference ReferenceType="HasProperty">ns=1;i=6001</Reference>
            </References>
          </UAObject>
          <UAVariable NodeId="ns=1;i=6001" BrowseName="ModelVersion" ParentNodeId="ns=1;i=5001" DataType="String">
            <DisplayName>ModelVersion</DisplayName>
            <References>
              <Reference ReferenceType="HasTypeDefinition">i=68</Reference>
            </References>
            <Value><String xmlns="http://opcfoundation.org/UA/2008/02/Types.xsd">1.0.0</String></Value>
          </UAVariable>
        </UANodeSet>
        """;

    [Fact]
    public async Task Upload_LeavesModelVersionAlone_FinishEditingFixesTheDataType()
    {
        await DeleteModelLinks();
        try
        {
            await ImportAsync();

            // Stored as authored: the upload did not rewrite the property.
            var afterUpload = await GetNode(ModelVersionNodeId);
            Assert.Equal(StringDataType, afterUpload.GetProperty("dataType").GetString());

            // Finish Editing settles the version, which refreshes NamespaceMetadata.
            var modelId = await GetModelIdAsync();
            var req = WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin");
            req.Content = JsonContent.Create(new { action = "keep" });
            var resp = await Client.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode,
                $"check-in failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");

            var afterKeep = await GetNode(ModelVersionNodeId);
            Assert.Equal(SemanticVersionString, afterKeep.GetProperty("dataType").GetString());
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    private async Task ImportAsync()
    {
        var req = WithServer(HttpMethod.Post, "/api/opcua/v1/namespaces/info/import");
        req.Content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(NodeSetXml)), "file", "scope.NodeSet2.xml" },
            { new StringContent("scope.NodeSet2.xml"), "fileName" },
            { new StringContent("0"), "chunkIndex" },
            { new StringContent("1"), "totalChunks" },
            { new StringContent("MIT"), "license" },
            { new StringContent("Test Copyright Holder"), "copyrightHolder" },
        };
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"import failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<JsonElement?> FindEntryAsync()
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entry = body.GetProperty("results").EnumerateArray()
            .FirstOrDefault(ns => ns.TryGetProperty("uri", out var uri)
                && string.Equals(uri.GetString(), ModelUri, StringComparison.OrdinalIgnoreCase));
        return entry.ValueKind == JsonValueKind.Undefined ? null : entry;
    }

    private async Task<string> GetModelIdAsync()
    {
        var entry = await FindEntryAsync();
        Assert.NotNull(entry);
        return entry.Value.GetProperty("id").GetString()!;
    }

    private async Task DeleteModelLinks()
    {
        for (var i = 0; i < 5; i++)
        {
            var entry = await FindEntryAsync();
            if (entry == null) break;
            var id = entry.Value.GetProperty("id").GetString();
            await Client.SendAsync(WithServer(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{id}"));
        }
        await PurgeModelRowsAsync(ModelUri);
    }
}
