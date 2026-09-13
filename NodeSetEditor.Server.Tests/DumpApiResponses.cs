using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

[Collection("Api")]
public class DumpApiResponses
{
    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public DumpApiResponses(ApiFixture fixture)
    {
        _client = fixture.Client;
        _workspaceUrn = fixture.WorkspaceUrn!;
    }

    private HttpRequestMessage WithServer(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("OpcUa-Server", _workspaceUrn);
        return request;
    }

    /// <summary>Encodes a NodeId as a base64url URL slug — see UaRestTestBase.Slug.</summary>
    private static string Slug(string nodeId) => NodeIdSlugFilter.EncodeBase64Url(nodeId);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [Fact]
    public async Task Dump_ObjectType_Node()
    {
        // BaseObjectType (i=58)
        var req = WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug("i=58")}");
        var resp = await _client.SendAsync(req);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Console.WriteLine("=== BaseObjectType (i=58) ===");
        Console.WriteLine(JsonSerializer.Serialize(body, Pretty));
    }

    [Fact]
    public async Task Dump_Variable_Node()
    {
        // ServerStatus (i=2256) - a Variable
        var req = WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug("i=2256")}");
        var resp = await _client.SendAsync(req);
        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Console.WriteLine("=== ServerStatus Variable (i=2256) ===");
            Console.WriteLine(JsonSerializer.Serialize(body, Pretty));
        }
        else
        {
            Console.WriteLine($"ServerStatus: {resp.StatusCode}");
        }
    }

    [Fact]
    public async Task Dump_DataType_Node()
    {
        // Int32 (i=6)
        var req = WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug("i=6")}");
        var resp = await _client.SendAsync(req);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Console.WriteLine("=== Int32 DataType (i=6) ===");
        Console.WriteLine(JsonSerializer.Serialize(body, Pretty));
    }

    [Fact]
    public async Task Dump_Children_ServerType()
    {
        // ServerType (i=2004) children
        var req = WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug("i=2004")}/children?full=true&count=5");
        var resp = await _client.SendAsync(req);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Console.WriteLine("=== ServerType (i=2004) Children (first 5) ===");
        Console.WriteLine(JsonSerializer.Serialize(body, Pretty));
    }

    [Fact]
    public async Task Dump_References_BaseObjectType()
    {
        var req = WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug("i=58")}/references");
        var resp = await _client.SendAsync(req);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Console.WriteLine("=== BaseObjectType (i=58) References ===");
        Console.WriteLine(JsonSerializer.Serialize(body, Pretty));
    }
}
