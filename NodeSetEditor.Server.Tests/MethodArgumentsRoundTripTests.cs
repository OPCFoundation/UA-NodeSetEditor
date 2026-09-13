using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Exercises the exact API calls the client's CreateArgumentsDialog makes:
/// create an InputArguments PropertyType variable (DataType=Argument i=296,
/// ValueRank=1) under a Method, PUT an Argument[] value, then read it back the
/// way the dialog pre-fills (list method children, read the property's embedded
/// Value). Also exercises the export → re-import path so the imported-data shape
/// (the realistic "existing properties" case) is locked too.
/// </summary>
[Collection("Api")]
public class MethodArgumentsRoundTripTests : UaRestTestBase
{
    public MethodArgumentsRoundTripTests(ApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task InputArguments_CreateAndReadBack()
    {
        // This test exports a model and re-imports it into a fresh workspace. Because
        // StoreNodeSetAsync replaces any existing model with the same (Uri, Version) and the
        // workspace-model FK cascades, re-importing the *shared* TestModel would delete it out
        // from under the other tests in this collection. So round-trip a dedicated, throwaway
        // model instead.
        const string argModelUri = "http://test.example.org/UA/MethodArgsRoundTrip/";
        var createModelReq = WithServer(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
        createModelReq.Content = JsonContent.Create(new { uri = argModelUri, name = "MethodArgsRoundTrip", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
        (await Client.SendAsync(createModelReq)).EnsureSuccessStatusCode();

        // ObjectType → Method
        var mathTypeId = await CreateChildNode("i=58", "ObjectType", "MathArgType", referenceTypeId: "i=45", modelUri: argModelUri);
        var methodId = await CreateChildNode(mathTypeId, "Method", "AddNumbers", referenceTypeId: "i=47", modelUri: argModelUri);

        // InputArguments property — exactly as CreateArgumentsDialog builds it.
        var createReq = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(methodId)}/children");
        createReq.Content = JsonContent.Create(new
        {
            modelUri = argModelUri,
            browseNameModelUri = "http://opcfoundation.org/UA/",
            nodeClass = "Variable",
            browseName = "InputArguments",
            displayName = "InputArguments",
            referenceTypeId = "i=46",
            typeDefinitionId = "i=68",
            dataType = "i=296",
            valueRank = 1,
        });
        var createResp = await Client.SendAsync(createReq);
        var createBody = await createResp.Content.ReadAsStringAsync();
        Assert.True(createResp.IsSuccessStatusCode,
            $"create InputArguments failed ({createResp.StatusCode}): {createBody}");
        var propId = JsonSerializer.Deserialize<JsonElement>(createBody).GetProperty("nodeId").GetString()!;

        // PUT the Argument[] value (PascalCase dicts to survive canonicalization).
        var args = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["UaTypeId"] = "i=296",
                ["Name"] = "a",
                ["DataType"] = "i=11",
                ["ValueRank"] = -1,
                ["ArrayDimensions"] = new int[0],
                ["Description"] = new Dictionary<string, object?> { ["Text"] = "first addend" },
            },
            new()
            {
                ["UaTypeId"] = "i=296",
                ["Name"] = "b",
                ["DataType"] = "i=11",
                ["ValueRank"] = -1,
                ["ArrayDimensions"] = new int[0],
                ["Description"] = new Dictionary<string, object?> { ["Text"] = "second addend" },
            },
        };
        var putReq = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(propId)}");
        putReq.Content = JsonContent.Create(new { value = args });
        var putResp = await Client.SendAsync(putReq);
        var putBody = await putResp.Content.ReadAsStringAsync();
        Assert.True(putResp.IsSuccessStatusCode, $"PUT value failed ({putResp.StatusCode}): {putBody}");

        // Read back exactly the way CreateArgumentsDialog seeds the form: list the
        // method's children with full=true and read the property's embedded Value
        // (the children response carries Value, so no separate fetch is needed).
        var childrenReq = WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug(methodId)}/children?full=true");
        var childrenResp = await Client.SendAsync(childrenReq);
        childrenResp.EnsureSuccessStatusCode();
        var children = await childrenResp.Content.ReadFromJsonAsync<JsonElement>();

        JsonElement? inputArgsChild = null;
        foreach (var c in children.GetProperty("results").EnumerateArray())
        {
            var bn = c.GetProperty("browseName").GetString() ?? "";
            var plain = bn.Contains(';') ? bn[(bn.IndexOf(';') + 1)..]
                : bn.Contains(':') ? bn[(bn.IndexOf(':') + 1)..] : bn;
            if (plain == "InputArguments") inputArgsChild = c;
        }
        Assert.True(inputArgsChild != null, "InputArguments not found among method children");

        // The children response must carry the Value the dialog reads.
        Assert.True(inputArgsChild.Value.TryGetProperty("value", out var value),
            "InputArguments child has no 'value' field: " + inputArgsChild.Value);
        Assert.Equal(JsonValueKind.Array, value.ValueKind);

        var items = value.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        // Lock the exact field contract CreateArgumentsDialog.valueToRows relies on:
        // inline PascalCase fields, NodeId as a plain string, LocalizedText as {Text}.
        Assert.Equal("a", items[0].GetProperty("Name").GetString());
        Assert.Equal("i=11", items[0].GetProperty("DataType").GetString());
        Assert.Equal(-1, items[0].GetProperty("ValueRank").GetInt32());
        Assert.Equal("first addend", items[0].GetProperty("Description").GetProperty("Text").GetString());
        Assert.Equal("b", items[1].GetProperty("Name").GetString());

        // A fresh single-node GET must agree with the embedded children Value.
        var node = await GetNode(inputArgsChild.Value.GetProperty("nodeId").GetString()!);
        Assert.Equal(value.ToString(), node.GetProperty("value").ToString());

        // ---- Now exercise the IMPORTED-data path (the realistic "existing
        // properties" case). Export this model to NodeSet XML and re-import it
        // into a fresh workspace, then read InputArguments back the same way.
        // The XML reader can encode struct fields differently than the JSON PUT
        // path (e.g. a NodeId field as a nested object) — this locks whatever
        // shape CreateArgumentsDialog.valueToRows must tolerate.
        var modelsReq = WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info");
        var modelsResp = await Client.SendAsync(modelsReq);
        modelsResp.EnsureSuccessStatusCode();
        var models = await modelsResp.Content.ReadFromJsonAsync<JsonElement>();
        string? testModelDbId = null;
        foreach (var m in models.GetProperty("results").EnumerateArray())
        {
            if (m.TryGetProperty("uri", out var uri) && uri.GetString() == argModelUri
                && m.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String)
                testModelDbId = idp.GetString();
        }
        Assert.True(testModelDbId != null, "test model id not found for export");

        var exportReq = WithServer(HttpMethod.Get,
            $"/api/opcua/v1/namespaces/info/{testModelDbId}/export?format=xml");
        var exportResp = await Client.SendAsync(exportReq);
        Assert.True(exportResp.IsSuccessStatusCode,
            $"export failed ({exportResp.StatusCode}): {await exportResp.Content.ReadAsStringAsync()}");
        var nodeSetXml = await exportResp.Content.ReadAsStringAsync();

        // Fresh workspace for the import.
        const string impUser = "args-import-001";
        const string impEmail = "args-import@test.net";
        var listReq = AsUser(HttpMethod.Get, "/api/opcua/v1/discovery", impUser, impEmail);
        var listResp = await Client.SendAsync(listReq);
        listResp.EnsureSuccessStatusCode();
        var listResults = (await listResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        foreach (var ws in listResults.EnumerateArray())
        {
            if (ws.GetProperty("applicationName").GetProperty("text").GetString() == "ArgsImportWs")
            {
                var urn = ws.GetProperty("applicationUri").GetString()!;
                await Client.SendAsync(AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(urn)}", impUser, impEmail));
            }
        }
        var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", impUser, impEmail);
        createWsReq.Content = JsonContent.Create(new { applicationName = "ArgsImportWs", description = "import" });
        var createWsResp = await Client.SendAsync(createWsReq);
        createWsResp.EnsureSuccessStatusCode();
        var impUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("applicationUri").GetString()!;

        try
        {
            var importReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info/import", impUser, impEmail);
            importReq.Headers.Add("OpcUa-Server", impUrn);
            var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(nodeSetXml));
            form.Add(fileContent, "file", "args.NodeSet2.xml");
            form.Add(new StringContent("args.NodeSet2.xml"), "fileName");
            form.Add(new StringContent("0"), "chunkIndex");
            form.Add(new StringContent("1"), "totalChunks");
            form.Add(new StringContent("MIT"), "license");
            form.Add(new StringContent("Test Copyright Holder"), "copyrightHolder");
            importReq.Content = form;
            var importResp = await Client.SendAsync(importReq);
            Assert.True(importResp.IsSuccessStatusCode,
                $"import failed ({importResp.StatusCode}): {await importResp.Content.ReadAsStringAsync()}");

            // NodeIds are namespace-qualified, so the method's nodeId is stable
            // across the export/import round trip — reuse it to read children.
            var impChildrenReq = AsUser(HttpMethod.Get,
                $"/api/opcua/v1/nodes/{Slug(methodId)}/children?full=true", impUser, impEmail);
            impChildrenReq.Headers.Add("OpcUa-Server", impUrn);
            var impChildrenResp = await Client.SendAsync(impChildrenReq);
            Assert.True(impChildrenResp.IsSuccessStatusCode,
                $"imported children failed ({impChildrenResp.StatusCode}): {await impChildrenResp.Content.ReadAsStringAsync()}");
            var impChildren = await impChildrenResp.Content.ReadFromJsonAsync<JsonElement>();

            JsonElement? impInput = null;
            foreach (var c in impChildren.GetProperty("results").EnumerateArray())
            {
                var bn = c.GetProperty("browseName").GetString() ?? "";
                var plain = bn.Contains(';') ? bn[(bn.IndexOf(';') + 1)..]
                    : bn.Contains(':') ? bn[(bn.IndexOf(':') + 1)..] : bn;
                if (plain == "InputArguments") impInput = c;
            }
            Assert.True(impInput != null, "imported InputArguments not found");
            Assert.True(impInput.Value.TryGetProperty("value", out var impValue),
                "imported InputArguments has no value: " + impInput.Value);

            // Surface the imported shape in the assertion message so the contract
            // is visible if it ever drifts.
            var impItems = impValue.EnumerateArray().ToList();
            Assert.True(impItems.Count == 2,
                $"expected 2 imported args, shape was: {impValue}");
            Assert.Equal("a", impItems[0].GetProperty("Name").GetString());
            // DataType may be a plain string OR a nested NodeId object after the
            // XML round trip — assert it carries i=11 either way.
            var dt = impItems[0].GetProperty("DataType");
            var dtStr = dt.ValueKind == JsonValueKind.String
                ? dt.GetString()
                : dt.TryGetProperty("Identifier", out var idf) ? idf.GetString()
                : dt.TryGetProperty("Id", out var id2) ? id2.GetString() : null;
            Assert.True(dtStr != null && dtStr.Contains("11"),
                $"imported DataType did not resolve to i=11; shape was: {dt}");
        }
        finally
        {
            await Client.SendAsync(AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(impUrn)}", impUser, impEmail));
        }
    }
}
