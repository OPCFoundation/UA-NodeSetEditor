using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The legacy DataTypeDictionaries (a BSD under OPCBinarySchema and an XSD under XmlSchema, each
/// with a DataTypeDescription per structure that the structure's encoding points at) follow the
/// model's DataTypes:
/// <list type="bullet">
///   <item>Generate DataType Dictionaries adds them to a model that has none, or rebuilds them.</item>
///   <item>Rebuilding never changes an existing NodeId: a renamed DataType keeps its description,
///     a deleted one loses it.</item>
///   <item>Finish Editing refreshes the dictionaries a model already has, and never adds them.</item>
///   <item>An upload is stored as authored — its dictionary is not touched until then.</item>
/// </list>
/// Everything is checked on the exported NodeSet, which is what a consumer of the model sees.
/// </summary>
[Collection("Api")]
public class DataTypeDictionaryTests : UaRestTestBase
{
    private const string OpcBinarySchema = "i=93";
    private const string XmlSchema = "i=92";

    public DataTypeDictionaryTests(ApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Generate_AddsBothDictionaries_ThenKeepsTheirNodeIdsThroughEdits()
    {
        const string uri = "http://test.example.org/UA/DictGenerate/";
        await CleanUpAsync(uri);
        try
        {
            var modelId = await CreateModelAsync(uri);
            var personType = await CreateStructureAsync(uri, "PersonType",
                new { name = "Name", dataType = "i=12" },
                new { name = "Age", dataType = "i=7" });

            // ---- Generate on a model without dictionaries adds a BSD and an XSD ----
            var first = await GenerateAsync(modelId);
            Assert.Equal(2, first.GetProperty("dictionariesCreated").GetInt32());
            Assert.Equal(2, first.GetProperty("descriptionsCreated").GetInt32());
            Assert.Equal(0, first.GetProperty("encodingsCreated").GetInt32()); // made with the type

            var model = await ExportAsync(uri);
            var bsd = model.Dictionary(OpcBinarySchema);
            var xsd = model.Dictionary(XmlSchema);
            Assert.Contains("<opc:StructuredType BaseType=\"ua:ExtensionObject\" Name=\"PersonType\">", bsd.Schema);
            Assert.Contains("<opc:Field Name=\"Age\" TypeName=\"opc:UInt32\" />", bsd.Schema);
            Assert.Contains("<xs:complexType name=\"PersonType\">", xsd.Schema);
            // A model with no XmlSchemaUri gets its NamespaceUri as the XML namespace.
            Assert.Equal(uri, XDocument.Parse(xsd.Schema).Root!.Attribute("targetNamespace")?.Value);

            // Both are named after the Namespace object, whose BrowseName is the namespace URI.
            Assert.EndsWith(":" + uri, bsd.BrowseName);
            Assert.Equal(bsd.BrowseName, xsd.BrowseName);
            if (model.NamespaceObjectBrowseName is { } nsName) Assert.Equal(nsName, bsd.BrowseName);

            var bsdDesc = Assert.Single(bsd.Descriptions);
            var xsdDesc = Assert.Single(xsd.Descriptions);
            Assert.Equal("PersonType", bsdDesc.Value);
            Assert.Equal("//xs:element[@name='PersonType']", xsdDesc.Value);
            // Each encoding points at the description in its own dictionary.
            Assert.Equal(bsdDesc.NodeId, model.DescriptionOf(model.Encoding("PersonType", "Default Binary")));
            Assert.Equal(xsdDesc.NodeId, model.DescriptionOf(model.Encoding("PersonType", "Default XML")));

            // ---- Generating again with nothing changed changes nothing ----
            var again = await GenerateAsync(modelId);
            Assert.Equal(0, again.GetProperty("dictionariesUpdated").GetInt32());
            Assert.Equal(0, again.GetProperty("descriptionsCreated").GetInt32());
            Assert.Equal(0, again.GetProperty("descriptionsUpdated").GetInt32());

            // ---- A new field rewrites the blobs, in place ----
            await UpdateDefinitionAsync(personType,
                new { name = "Name", dataType = "i=12" },
                new { name = "Age", dataType = "i=7" },
                new { name = "Nicknames", dataType = "i=12", valueRank = 1 });
            var afterField = await GenerateAsync(modelId);
            Assert.Equal(2, afterField.GetProperty("dictionariesUpdated").GetInt32());

            model = await ExportAsync(uri);
            Assert.Equal(bsd.NodeId, model.Dictionary(OpcBinarySchema).NodeId);
            Assert.Equal(xsd.NodeId, model.Dictionary(XmlSchema).NodeId);
            Assert.Contains("<opc:Field Name=\"NoOfNicknames\" TypeName=\"opc:Int32\" />", model.Dictionary(OpcBinarySchema).Schema);

            // ---- A rename keeps the descriptions, with the new name written into them ----
            await RenameAsync(personType, "HumanType");
            var afterRename = await GenerateAsync(modelId);
            Assert.Equal(2, afterRename.GetProperty("descriptionsUpdated").GetInt32());
            Assert.Equal(0, afterRename.GetProperty("descriptionsCreated").GetInt32());

            model = await ExportAsync(uri);
            var renamedBsd = Assert.Single(model.Dictionary(OpcBinarySchema).Descriptions);
            var renamedXsd = Assert.Single(model.Dictionary(XmlSchema).Descriptions);
            Assert.Equal(bsdDesc.NodeId, renamedBsd.NodeId);
            Assert.Equal(xsdDesc.NodeId, renamedXsd.NodeId);
            Assert.Equal("HumanType", renamedBsd.Value);
            Assert.Equal("//xs:element[@name='HumanType']", renamedXsd.Value);
            Assert.EndsWith("HumanType", renamedBsd.BrowseName);

            // ---- Deleting the DataType takes its encodings, then its descriptions ----
            await DeleteNodeAsync(personType);
            var afterDelete = await GenerateAsync(modelId);
            Assert.Equal(2, afterDelete.GetProperty("descriptionsDeleted").GetInt32());

            model = await ExportAsync(uri);
            Assert.Empty(model.Dictionary(OpcBinarySchema).Descriptions);
            Assert.Empty(model.Dictionary(XmlSchema).Descriptions);
            Assert.Empty(model.EncodingObjects);
            Assert.DoesNotContain("HumanType", model.Dictionary(OpcBinarySchema).Schema);
        }
        finally
        {
            await CleanUpAsync(uri);
        }
    }

    [Fact]
    public async Task Generate_OnAReadOnlyModel_IsRefused()
    {
        const string uri = "http://test.example.org/UA/DictReadOnly/";
        await CleanUpAsync(uri);
        try
        {
            var modelId = await CreateModelAsync(uri);
            await CreateStructureAsync(uri, "LockedType", new { name = "A", dataType = "i=6" });
            await CheckinAsync(modelId, "keep");

            var resp = await Client.SendAsync(
                WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{await ModelIdAsync(uri)}/datatype-dictionaries"));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

            var model = await ExportAsync(uri);
            Assert.Empty(model.Dictionaries);
        }
        finally
        {
            await CleanUpAsync(uri);
        }
    }

    [Fact]
    public async Task FinishEditing_RefreshesExistingDictionaries_AndNeverAddsThem()
    {
        const string withoutUri = "http://test.example.org/UA/DictFinishNone/";
        const string withUri = "http://test.example.org/UA/DictFinishSome/";
        await CleanUpAsync(withoutUri);
        await CleanUpAsync(withUri);
        try
        {
            // A model without dictionaries is not given any by Finish Editing.
            var withoutId = await CreateModelAsync(withoutUri);
            await CreateStructureAsync(withoutUri, "PlainType", new { name = "A", dataType = "i=6" });
            await CheckinAsync(withoutId, "keep");
            Assert.Empty((await ExportAsync(withoutUri)).Dictionaries);

            // A model with dictionaries has them brought up to date by it.
            var withId = await CreateModelAsync(withUri);
            var type = await CreateStructureAsync(withUri, "TrackedType", new { name = "A", dataType = "i=6" });
            await GenerateAsync(withId);
            await UpdateDefinitionAsync(type,
                new { name = "A", dataType = "i=6" },
                new { name = "Added", dataType = "i=11" });
            Assert.DoesNotContain("Name=\"Added\"", (await ExportAsync(withUri)).Dictionary(OpcBinarySchema).Schema);

            await CheckinAsync(withId, "keep");

            var model = await ExportAsync(withUri);
            Assert.Contains("<opc:Field Name=\"Added\" TypeName=\"opc:Double\" />", model.Dictionary(OpcBinarySchema).Schema);
            Assert.Contains("name=\"Added\"", model.Dictionary(XmlSchema).Schema);
        }
        finally
        {
            await CleanUpAsync(withoutUri);
            await CleanUpAsync(withUri);
        }
    }

    [Fact]
    public async Task Upload_StoresTheDictionaryAsAuthored_UntilFinishEditing()
    {
        const string uri = "http://test.example.org/UA/DictUpload/";
        await CleanUpAsync(uri);
        try
        {
            // A deliberately stale dictionary: it describes a type the NodeSet does not have.
            var staleBsd =
                $"<opc:TypeDictionary xmlns:opc=\"http://opcfoundation.org/BinarySchema/\" TargetNamespace=\"{uri}\">" +
                "<opc:StructuredType Name=\"Gone\" /></opc:TypeDictionary>";
            await ImportAsync(UploadNodeSet(uri, staleBsd));

            var uploadedModel = await ExportAsync(uri);
            // No XML dictionary, so no XmlSchemaUri is derived on load.
            Assert.Null(uploadedModel.XmlSchemaUri);
            var uploaded = uploadedModel.Dictionary(OpcBinarySchema);
            Assert.Equal(staleBsd, uploaded.Schema);
            Assert.Equal("Gone", Assert.Single(uploaded.Descriptions).Value);

            await CheckinAsync(await ModelIdAsync(uri), "keep");

            var model = await ExportAsync(uri);
            var bsd = model.Dictionary(OpcBinarySchema);
            Assert.Equal(uploaded.NodeId, bsd.NodeId);
            Assert.Contains("Name=\"UploadedType\"", bsd.Schema);
            var desc = Assert.Single(bsd.Descriptions);
            Assert.Equal("UploadedType", desc.Value);
            // The stale description is gone and the type's encoding now has one to point at.
            Assert.Equal(desc.NodeId, model.DescriptionOf(model.Encoding("UploadedType", "Default Binary")));
        }
        finally
        {
            await CleanUpAsync(uri);
        }
    }

    [Fact]
    public async Task XmlSchemaUri_IsStored_AndBecomesTheXmlDictionarysNamespace()
    {
        const string uri = "http://test.example.org/UA/DictXsdUri/";
        const string xmlSchemaUri = "http://test.example.org/UA/DictXsdUri/Custom.xsd";
        await CleanUpAsync(uri);
        try
        {
            // The uploaded XML dictionary declares a different namespace than the model does.
            await ImportAsync(XmlSchemaUriNodeSet(uri, xmlSchemaUri, staleNamespace: uri + "Types.xsd"));

            // The model's XmlSchemaUri survives the round trip through the DB.
            var uploaded = await ExportAsync(uri);
            Assert.Equal(xmlSchemaUri, uploaded.XmlSchemaUri);

            await GenerateAsync(await ModelIdAsync(uri));

            var model = await ExportAsync(uri);
            Assert.Equal(xmlSchemaUri, model.XmlSchemaUri);
            var xsd = model.Dictionary(XmlSchema);
            Assert.Equal(uploaded.Dictionary(XmlSchema).NodeId, xsd.NodeId);
            Assert.Equal(xmlSchemaUri, XDocument.Parse(xsd.Schema).Root!.Attribute("targetNamespace")?.Value);
            Assert.Equal(xmlSchemaUri, model.NamespaceUriOf(xsd.NodeId));
            // The binary dictionary is not affected: its namespace is the model URI.
            Assert.Equal(uri, model.NamespaceUriOf(model.Dictionary(OpcBinarySchema).NodeId));
        }
        finally
        {
            await CleanUpAsync(uri);
        }
    }

    [Fact]
    public async Task WithoutXmlSchemaUri_TheXmlDictionarysNamespaceUriIsAdoptedOnLoad()
    {
        const string uri = "http://test.example.org/UA/DictXsdDefault/";
        const string published = uri + "Types.xsd"; // what ModelCompiler-built companion specs ship
        await CleanUpAsync(uri);
        try
        {
            // No XmlSchemaUri declared: the XML dictionary's NamespaceUri becomes the model's.
            await ImportAsync(XmlSchemaUriNodeSet(uri, xmlSchemaUri: null, staleNamespace: published));
            Assert.Equal(published, (await ExportAsync(uri)).XmlSchemaUri);

            // Regenerating keeps that namespace rather than falling back to the model URI.
            await GenerateAsync(await ModelIdAsync(uri));
            var model = await ExportAsync(uri);
            Assert.Equal(published, model.XmlSchemaUri);
            var xsd = model.Dictionary(XmlSchema);
            Assert.Equal(published, XDocument.Parse(xsd.Schema).Root!.Attribute("targetNamespace")?.Value);
            Assert.Equal(published, model.NamespaceUriOf(xsd.NodeId));
        }
        finally
        {
            await CleanUpAsync(uri);
        }
    }

    [Fact]
    public async Task Download_WithDictionaries_ZipsTheModelsBsdAndXsd_ButNoDependencysDictionaries()
    {
        const string uri = "http://test.example.org/UA/DictDownload/";
        await CleanUpAsync(uri);
        try
        {
            var modelId = await CreateModelAsync(uri);
            await CreateStructureAsync(uri, "ZippedType", new { name = "A", dataType = "i=6" });

            // No stored dictionaries: they are generated for the download, the model is not changed.
            var alone = await DownloadZipAsync(modelId, "includeDictionaries=true");
            Assert.Equal(new[] { ".bsd", ".xml", ".xsd" }, alone.Keys.Select(Path.GetExtension).OrderBy(e => e));
            Assert.Contains("Name=\"ZippedType\"", alone.Single(e => e.Key.EndsWith(".bsd")).Value);
            Assert.Contains("name=\"ZippedType\"", alone.Single(e => e.Key.EndsWith(".xsd")).Value);
            Assert.Empty((await ExportAsync(uri)).Dictionaries);

            // Stored dictionaries are shipped as stored.
            await GenerateAsync(modelId);
            var model = await ExportAsync(uri);
            var stored = await DownloadZipAsync(modelId, "includeDictionaries=true");
            Assert.Equal(model.Dictionary(OpcBinarySchema).Schema, stored.Single(e => e.Key.EndsWith(".bsd")).Value);
            Assert.Equal(model.Dictionary(XmlSchema).Schema, stored.Single(e => e.Key.EndsWith(".xsd")).Value);

            // With dependencies: every dependency NodeSet, but only this model's dictionaries.
            var depsOnly = await DownloadZipAsync(modelId, "includeDependencies=true");
            var both = await DownloadZipAsync(modelId, "includeDependencies=true&includeDictionaries=true");
            Assert.Equal(
                depsOnly.Keys.Concat(stored.Keys.Where(k => !k.EndsWith(".xml"))).OrderBy(k => k),
                both.Keys.OrderBy(k => k));
            Assert.Single(both.Keys, k => k.EndsWith(".bsd"));
            Assert.Single(both.Keys, k => k.EndsWith(".xsd"));
        }
        finally
        {
            await CleanUpAsync(uri);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Dictionary<string, string>> DownloadZipAsync(string modelId, string query)
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Get,
            $"/api/opcua/v1/namespaces/info/{modelId}/export?format=xml&{query}"));
        Assert.True(resp.IsSuccessStatusCode, $"download failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        Assert.Equal("application/zip", resp.Content.Headers.ContentType?.MediaType);
        using var zip = new System.IO.Compression.ZipArchive(await resp.Content.ReadAsStreamAsync());
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    private static string XmlSchemaUriNodeSet(string uri, string? xmlSchemaUri, string staleNamespace) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
          <NamespaceUris><Uri>{uri}</Uri></NamespaceUris>
          <Models>
            <Model ModelUri="{uri}" {(xmlSchemaUri != null ? $"XmlSchemaUri=\"{xmlSchemaUri}\"" : "")} Version="1.0.0" ModelVersion="1.0.0" PublicationDate="2026-01-01T00:00:00Z">
              <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.03" PublicationDate="2023-12-15T00:00:00Z" />
            </Model>
          </Models>
          <Aliases>
            <Alias Alias="Int32">i=6</Alias>
            <Alias Alias="String">i=12</Alias>
            <Alias Alias="ByteString">i=15</Alias>
            <Alias Alias="HasComponent">i=47</Alias>
            <Alias Alias="HasProperty">i=46</Alias>
            <Alias Alias="HasSubtype">i=45</Alias>
            <Alias Alias="HasTypeDefinition">i=40</Alias>
          </Aliases>
          <UADataType NodeId="ns=1;i=3001" BrowseName="1:XsdType">
            <DisplayName>XsdType</DisplayName>
            <References>
              <Reference ReferenceType="HasSubtype" IsForward="false">i=22</Reference>
            </References>
            <Definition Name="1:XsdType">
              <Field Name="Count" DataType="Int32" />
            </Definition>
          </UADataType>
          <UAVariable NodeId="ns=1;i=6001" BrowseName="1:TypeDictionary" DataType="ByteString">
            <DisplayName>TypeDictionary</DisplayName>
            <References>
              <Reference ReferenceType="HasComponent" IsForward="false">i=92</Reference>
              <Reference ReferenceType="HasTypeDefinition">i=72</Reference>
              <Reference ReferenceType="HasProperty">ns=1;i=6002</Reference>
            </References>
            <Value><ByteString xmlns="http://opcfoundation.org/UA/2008/02/Types.xsd">{Convert.ToBase64String(Encoding.UTF8.GetBytes("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" />"))}</ByteString></Value>
          </UAVariable>
          <UAVariable NodeId="ns=1;i=6002" BrowseName="NamespaceUri" ParentNodeId="ns=1;i=6001" DataType="String">
            <DisplayName>NamespaceUri</DisplayName>
            <References>
              <Reference ReferenceType="HasTypeDefinition">i=68</Reference>
            </References>
            <Value><String xmlns="http://opcfoundation.org/UA/2008/02/Types.xsd">{staleNamespace}</String></Value>
          </UAVariable>
        </UANodeSet>
        """;

    private static string UploadNodeSet(string uri, string bsd) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
          <NamespaceUris><Uri>{uri}</Uri></NamespaceUris>
          <Models>
            <Model ModelUri="{uri}" Version="1.0.0" ModelVersion="1.0.0" PublicationDate="2026-01-01T00:00:00Z">
              <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.03" PublicationDate="2023-12-15T00:00:00Z" />
            </Model>
          </Models>
          <Aliases>
            <Alias Alias="Int32">i=6</Alias>
            <Alias Alias="String">i=12</Alias>
            <Alias Alias="ByteString">i=15</Alias>
            <Alias Alias="HasComponent">i=47</Alias>
            <Alias Alias="HasProperty">i=46</Alias>
            <Alias Alias="HasSubtype">i=45</Alias>
            <Alias Alias="HasEncoding">i=38</Alias>
            <Alias Alias="HasDescription">i=39</Alias>
            <Alias Alias="HasTypeDefinition">i=40</Alias>
          </Aliases>
          <UADataType NodeId="ns=1;i=3001" BrowseName="1:UploadedType">
            <DisplayName>UploadedType</DisplayName>
            <References>
              <Reference ReferenceType="HasSubtype" IsForward="false">i=22</Reference>
              <Reference ReferenceType="HasEncoding">ns=1;i=5001</Reference>
            </References>
            <Definition Name="1:UploadedType">
              <Field Name="Count" DataType="Int32" />
            </Definition>
          </UADataType>
          <UAObject NodeId="ns=1;i=5001" BrowseName="Default Binary">
            <DisplayName>Default Binary</DisplayName>
            <References>
              <Reference ReferenceType="HasTypeDefinition">i=76</Reference>
            </References>
          </UAObject>
          <UAVariable NodeId="ns=1;i=6001" BrowseName="1:TypeDictionary" DataType="ByteString">
            <DisplayName>TypeDictionary</DisplayName>
            <References>
              <Reference ReferenceType="HasComponent" IsForward="false">i=93</Reference>
              <Reference ReferenceType="HasTypeDefinition">i=72</Reference>
              <Reference ReferenceType="HasComponent">ns=1;i=6002</Reference>
            </References>
            <Value><ByteString xmlns="http://opcfoundation.org/UA/2008/02/Types.xsd">{Convert.ToBase64String(Encoding.UTF8.GetBytes(bsd))}</ByteString></Value>
          </UAVariable>
          <UAVariable NodeId="ns=1;i=6002" BrowseName="1:Gone" ParentNodeId="ns=1;i=6001" DataType="String">
            <DisplayName>Gone</DisplayName>
            <References>
              <Reference ReferenceType="HasTypeDefinition">i=69</Reference>
            </References>
            <Value><String xmlns="http://opcfoundation.org/UA/2008/02/Types.xsd">Gone</String></Value>
          </UAVariable>
        </UANodeSet>
        """;

    private async Task<string> CreateModelAsync(string uri)
    {
        var req = WithServer(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
        req.Content = JsonContent.Create(new
        {
            uri,
            name = uri.TrimEnd('/').Split('/').Last(), // model names are unique per workspace
            license = "MIT",
            copyrightHolder = "Test Copyright Holder",
        });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode, $"create failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateStructureAsync(string uri, string name, params object[] fields)
    {
        var req = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=22")}/children");
        req.Content = JsonContent.Create(new
        {
            modelUri = uri,
            nodeClass = "DataType",
            browseName = name,
            displayName = name,
            referenceTypeId = "i=45",
        });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"create DataType failed ({resp.StatusCode}): {body}");
        var nodeId = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("nodeId").GetString()!;
        await UpdateDefinitionAsync(nodeId, fields);
        return nodeId;
    }

    private async Task UpdateDefinitionAsync(string nodeId, params object[] fields)
    {
        var req = WithServer(HttpMethod.Put, $"/api/opcua/v1/types/data-types/{Slug(nodeId)}/definition");
        req.Content = JsonContent.Create(new { fields });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode, $"definition failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task RenameAsync(string nodeId, string browseName)
    {
        var req = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
        req.Content = JsonContent.Create(new { browseName, displayName = browseName });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode, $"rename failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task DeleteNodeAsync(string nodeId)
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Delete, $"/api/opcua/v1/nodes/{Slug(nodeId)}"));
        Assert.True(resp.IsSuccessStatusCode, $"delete failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<JsonElement> GenerateAsync(string modelId)
    {
        var resp = await Client.SendAsync(
            WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/datatype-dictionaries"));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"generate failed ({resp.StatusCode}): {body}");
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
        return result;
    }

    private async Task CheckinAsync(string modelId, string action)
    {
        var req = WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin");
        req.Content = JsonContent.Create(new { action });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode, $"check-in failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task ImportAsync(string xml)
    {
        var req = WithServer(HttpMethod.Post, "/api/opcua/v1/namespaces/info/import");
        req.Content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(xml)), "file", "dict.NodeSet2.xml" },
            { new StringContent("dict.NodeSet2.xml"), "fileName" },
            { new StringContent("0"), "chunkIndex" },
            { new StringContent("1"), "totalChunks" },
            { new StringContent("MIT"), "license" },
            { new StringContent("Test Copyright Holder"), "copyrightHolder" },
        };
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode, $"import failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<JsonElement?> FindEntryAsync(string uri)
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entry = body.GetProperty("results").EnumerateArray()
            .FirstOrDefault(ns => ns.TryGetProperty("uri", out var u)
                && string.Equals(u.GetString(), uri, StringComparison.OrdinalIgnoreCase));
        return entry.ValueKind == JsonValueKind.Undefined ? null : entry;
    }

    private async Task<string> ModelIdAsync(string uri)
    {
        var entry = await FindEntryAsync(uri);
        Assert.NotNull(entry);
        return entry.Value.GetProperty("id").GetString()!;
    }

    private async Task CleanUpAsync(string uri)
    {
        for (var i = 0; i < 5; i++)
        {
            var entry = await FindEntryAsync(uri);
            if (entry == null) break;
            await Client.SendAsync(WithServer(HttpMethod.Delete,
                $"/api/opcua/v1/namespaces/info/{entry.Value.GetProperty("id").GetString()}"));
        }
        await PurgeModelRowsAsync(uri);
    }

    private async Task<ExportedModel> ExportAsync(string uri)
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Get,
            $"/api/opcua/v1/namespaces/info/{await ModelIdAsync(uri)}/export?format=xml"));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"export failed ({resp.StatusCode}): {body}");
        return new ExportedModel(XDocument.Parse(body));
    }

    // ------------------------------------------------------------------ export reader

    private sealed record Description(string NodeId, string BrowseName, string? Value);
    private sealed record DictionaryNode(string NodeId, string BrowseName, string Schema, List<Description> Descriptions);

    /// <summary>The dictionary-related parts of an exported NodeSet, with aliases resolved.</summary>
    private sealed class ExportedModel
    {
        private static readonly XNamespace Ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
        private sealed record Ref(string Type, bool Forward, string Target);
        private sealed record Node(XElement Element, string NodeId, string BrowseName, string? ParentNodeId, List<Ref> Refs);

        private readonly List<Node> _nodes;

        /// <summary>The XmlSchemaUri on the exported Model element.</summary>
        public string? XmlSchemaUri { get; }

        public ExportedModel(XDocument doc)
        {
            XmlSchemaUri = doc.Root!.Element(Ua + "Models")?.Element(Ua + "Model")?.Attribute("XmlSchemaUri")?.Value;
            var aliases = doc.Root!.Element(Ua + "Aliases")?.Elements(Ua + "Alias")
                .ToDictionary(a => a.Attribute("Alias")!.Value, a => a.Value.Trim()) ?? new();
            string Resolve(string? v) => v != null && aliases.TryGetValue(v, out var id) ? id : v ?? "";

            _nodes = doc.Root.Elements()
                .Where(e => e.Attribute("NodeId") != null)
                .Select(e => new Node(e,
                    e.Attribute("NodeId")!.Value,
                    e.Attribute("BrowseName")?.Value ?? "",
                    e.Attribute("ParentNodeId")?.Value,
                    e.Element(Ua + "References")?.Elements(Ua + "Reference")
                        .Select(r => new Ref(Resolve(r.Attribute("ReferenceType")?.Value),
                            r.Attribute("IsForward")?.Value != "false", r.Value.Trim()))
                        .ToList() ?? new()))
                .ToList();
        }

        private bool Has(Node n, string type, bool forward, string target) =>
            n.Refs.Any(r => r.Type == type && r.Forward == forward && r.Target == target);

        /// <summary>Whether a reference exists, stated on either end.</summary>
        private bool Linked(string source, string type, string target) =>
            _nodes.Any(n => n.NodeId == source && Has(n, type, true, target))
            || _nodes.Any(n => n.NodeId == target && Has(n, type, false, source));

        private static string? Text(XElement e) => e.Element(Ua + "Value")?.Elements().FirstOrDefault()?.Value;

        public List<DictionaryNode> Dictionaries => _nodes
            .Where(n => n.Element.Name.LocalName == "UAVariable" && Has(n, "i=40", true, "i=72"))
            .Select(ToDictionary)
            .ToList();

        public DictionaryNode Dictionary(string folder) => Assert.Single(_nodes
            .Where(n => n.Element.Name.LocalName == "UAVariable" && Has(n, "i=40", true, "i=72")
                && (Has(n, "i=47", false, folder) || n.ParentNodeId == folder))
            .Select(ToDictionary));

        private DictionaryNode ToDictionary(Node dict)
        {
            var bytes = Convert.FromBase64String(string.Concat((Text(dict.Element) ?? "").Where(c => !char.IsWhiteSpace(c))));
            var descriptions = _nodes
                .Where(n => Has(n, "i=40", true, "i=69")
                    && (n.ParentNodeId == dict.NodeId || Linked(dict.NodeId, "i=47", n.NodeId)))
                .Select(n => new Description(n.NodeId, n.BrowseName, Text(n.Element)))
                .ToList();
            return new DictionaryNode(dict.NodeId, dict.BrowseName, System.Text.Encoding.UTF8.GetString(bytes), descriptions);
        }

        /// <summary>The value of a dictionary's NamespaceUri property.</summary>
        public string? NamespaceUriOf(string dictionaryId) => Text(Assert.Single(_nodes.Where(n =>
            n.BrowseName == "NamespaceUri"
            && (n.ParentNodeId == dictionaryId || Linked(dictionaryId, "i=46", n.NodeId)))).Element);

        /// <summary>The BrowseName of the model's NamespaceMetadataType object, if it has one.</summary>
        public string? NamespaceObjectBrowseName => _nodes
            .Where(n => n.Element.Name.LocalName == "UAObject" && Has(n, "i=40", true, "i=11616"))
            .Select(n => n.BrowseName).SingleOrDefault();

        public List<string> EncodingObjects => _nodes
            .Where(n => n.Element.Name.LocalName == "UAObject" && Has(n, "i=40", true, "i=76"))
            .Select(n => n.NodeId).ToList();

        public string Encoding(string typeName, string encodingName)
        {
            var type = Assert.Single(_nodes.Where(n => n.Element.Name.LocalName == "UADataType"
                && n.BrowseName.EndsWith(":" + typeName, StringComparison.Ordinal)));
            return Assert.Single(_nodes.Where(n => n.BrowseName == encodingName
                && Linked(type.NodeId, "i=38", n.NodeId)).Select(n => n.NodeId));
        }

        public string? DescriptionOf(string encodingId) =>
            _nodes.Where(n => Has(n, "i=40", true, "i=69") && Linked(encodingId, "i=39", n.NodeId))
                .Select(n => n.NodeId).SingleOrDefault();
    }
}
