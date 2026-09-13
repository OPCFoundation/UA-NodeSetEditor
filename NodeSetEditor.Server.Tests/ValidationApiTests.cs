using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Integration tests for the specification-validation feature: per-workspace document
/// upload/list/delete, ownership/IDOR + the private-model gate at job start, the job lifecycle
/// (with the applied-model URI shown on the document), and the Pop/Push worker contract
/// (including interruption semantics). Idempotent/self-cleaning against the shared Postgres —
/// every test deletes the documents it creates (jobs cascade with them).
/// </summary>
[Collection("Api")]
public class ValidationApiTests : UaRestTestBase
{
    public ValidationApiTests(ApiFixture fixture) : base(fixture) { }

    private const string Base = "/api/opcua/v1/validation";

    // ---------------------------------------------------------------- helpers

    private async Task<string> GetTestModelIdAsync()
    {
        var req = WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info");
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var ns in body.GetProperty("results").EnumerateArray())
        {
            if (ns.TryGetProperty("uri", out var uri) && uri.GetString() == ApiFixture.TestModelUri
                && ns.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
                return id.GetString()!;
        }
        throw new InvalidOperationException("Test model not found.");
    }

    private async Task<HttpResponseMessage> UploadChunkAsync(
        string fileName, byte[] bytes, int chunkIndex, int totalChunks, string? uploadId)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", fileName },
            { new StringContent(fileName), "fileName" },
            { new StringContent(chunkIndex.ToString()), "chunkIndex" },
            { new StringContent(totalChunks.ToString()), "totalChunks" },
        };
        if (uploadId != null) form.Add(new StringContent(uploadId), "uploadId");

        var req = WithServer(HttpMethod.Post, $"{Base}/documents/upload");
        req.Content = form;
        return await Client.SendAsync(req);
    }

    private async Task<string> UploadSingleAsync(string fileName, byte[]? bytes = null)
    {
        var resp = await UploadChunkAsync(fileName, bytes ?? Encoding.UTF8.GetBytes("docx-bytes"), 0, 1, null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"upload failed ({resp.StatusCode}): {body}");
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.True(result.GetProperty("isComplete").GetBoolean());
        return result.GetProperty("documentId").GetString()!;
    }

    private async Task<JsonElement> ListDocsAsync()
    {
        var req = WithServer(HttpMethod.Get, $"{Base}/documents");
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task DeleteDocAsync(string documentId)
    {
        var req = WithServer(HttpMethod.Delete, $"{Base}/documents/{documentId}");
        (await Client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<JsonElement?> FindDocAsync(string documentId)
    {
        var docs = await ListDocsAsync();
        foreach (var d in docs.EnumerateArray())
            if (d.GetProperty("id").GetString() == documentId) return d;
        return null;
    }

    private async Task<HttpResponseMessage> StartJobRawAsync(string documentId, string modelId)
        => await Client.SendAsync(WithServer(HttpMethod.Post, $"{Base}/documents/{documentId}/jobs?modelId={modelId}"));

    private async Task<string> StartJobAsync(string documentId, string modelId)
    {
        var resp = await StartJobRawAsync(documentId, modelId);
        resp.EnsureSuccessStatusCode();
        var job = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return job.GetProperty("id").GetString()!;
    }

    private HttpClient WorkerClient()
    {
        var c = Fixture.CreateAnonymousClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", ApiFixture.WorkerApiKey);
        return c;
    }

    private static async Task<JsonElement?> PopAsync(HttpClient worker, string workerId = "w1")
    {
        var resp = await worker.PostAsJsonAsync($"{Base}/worker/jobs/pop", new { workerId });
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> PushAsync(
        HttpClient worker, string jobId, string lockToken, int exitCode, string entriesJson)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(lockToken), "lockToken" },
            { new StringContent(exitCode.ToString()), "exitCode" },
            { new StringContent("1"), "errorCount" },
            { new StringContent("2"), "warningCount" },
            { new StringContent("0"), "infoCount" },
            { new StringContent("1 error(s), 2 warning(s), 0 info"), "summary" },
            { new StringContent(entriesJson), "entries" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("=== validate ===\n")), "log", "validation.log" },
        };
        return await worker.PostAsync($"{Base}/worker/jobs/{jobId}/push", form);
    }

    /// <summary>Pop repeatedly until the given job surfaces (drains unrelated queued jobs safely).</summary>
    private static async Task<string> PopUntilAsync(HttpClient worker, string jobId)
    {
        for (var i = 0; i < 20; i++)
        {
            var job = await PopAsync(worker);
            if (job == null) break;
            if (job.Value.GetProperty("jobId").GetString() == jobId)
                return job.Value.GetProperty("lockToken").GetString()!;
        }
        throw new InvalidOperationException("Job was not returned by pop.");
    }

    // ---------------------------------------------------------------- documents

    [Fact]
    public async Task Upload_List_Delete_Document()
    {
        var docId = await UploadSingleAsync("spec-upload.docx");
        try
        {
            var doc = await FindDocAsync(docId);
            Assert.NotNull(doc);
            Assert.Equal("spec-upload.docx", doc!.Value.GetProperty("fileName").GetString());
            Assert.Equal("Ready", doc.Value.GetProperty("status").GetString());
        }
        finally { await DeleteDocAsync(docId); }

        Assert.Null(await FindDocAsync(docId));
    }

    [Fact]
    public async Task MultiChunk_Upload_Assembles()
    {
        var a = Encoding.UTF8.GetBytes(new string('a', 1000));
        var b = Encoding.UTF8.GetBytes(new string('b', 500));

        var r0 = await UploadChunkAsync("multi.docx", a, 0, 2, null);
        r0.EnsureSuccessStatusCode();
        var uploadId = (await r0.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString();

        var r1 = await UploadChunkAsync("multi.docx", b, 1, 2, uploadId);
        r1.EnsureSuccessStatusCode();
        var final = await r1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(final.GetProperty("isComplete").GetBoolean());
        var docId = final.GetProperty("documentId").GetString()!;
        try
        {
            var doc = await FindDocAsync(docId);
            Assert.Equal(1500, doc!.Value.GetProperty("sizeBytes").GetInt64());
        }
        finally { await DeleteDocAsync(docId); }
    }

    [Fact]
    public async Task Upload_NonDocx_Rejected()
    {
        var resp = await UploadChunkAsync("notes.txt", Encoding.UTF8.GetBytes("x"), 0, 1, null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task StartJob_UnknownModel_Returns404()
    {
        var docId = await UploadSingleAsync("unknown-model.docx");
        try
        {
            // The selected model is not in this workspace → 404 (private-model gate / IDOR).
            var resp = await StartJobRawAsync(docId, Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await DeleteDocAsync(docId); }
    }

    [Fact]
    public async Task ForeignUser_CannotListDocuments()
    {
        var docId = await UploadSingleAsync("private.docx");
        try
        {
            // A different user has no access to this workspace → 404 (existence not leaked).
            var req = AsUser(HttpMethod.Get, $"{Base}/documents", "stranger-001", "stranger@example.com");
            req.Headers.Add("OpcUa-Server", WorkspaceUrn);
            var resp = await Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await DeleteDocAsync(docId); }
    }

    // ---------------------------------------------------------------- job lifecycle

    [Fact]
    public async Task JobLifecycle_PopPush_Completes()
    {
        var modelId = await GetTestModelIdAsync();
        var docId = await UploadSingleAsync("lifecycle.docx");
        try
        {
            var jobId = await StartJobAsync(docId, modelId);
            var queued = await FindDocAsync(docId);
            Assert.Equal("Queued", queued!.Value.GetProperty("status").GetString());
            // The applied model URI is shown on the document once a job is started.
            Assert.Equal(ApiFixture.TestModelUri, queued.Value.GetProperty("modelUri").GetString());

            var worker = WorkerClient();
            var lockToken = await PopUntilAsync(worker, jobId);
            Assert.Equal("Running", (await FindDocAsync(docId))!.Value.GetProperty("status").GetString());

            var entries = "[{\"section\":\"6.1\",\"table\":\"T1\",\"severity\":\"Error\",\"code\":\"NodeNotFound\",\"description\":\"x\"}]";
            var push = await PushAsync(worker, jobId, lockToken, exitCode: 0, entries);
            Assert.Equal(HttpStatusCode.OK, push.StatusCode);

            var doc = await FindDocAsync(docId);
            Assert.Equal("Completed", doc!.Value.GetProperty("status").GetString());
            Assert.Equal(1, doc.Value.GetProperty("errorCount").GetInt32());

            var resReq = WithServer(HttpMethod.Get, $"{Base}/jobs/{jobId}/result");
            var resResp = await Client.SendAsync(resReq);
            resResp.EnsureSuccessStatusCode();
            var result = await resResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("NodeNotFound", result.GetProperty("entries")[0].GetProperty("code").GetString());

            // Reset → Ready, and the applied model is cleared.
            var reset = WithServer(HttpMethod.Post, $"{Base}/jobs/{jobId}/reset");
            (await Client.SendAsync(reset)).EnsureSuccessStatusCode();
            var afterReset = await FindDocAsync(docId);
            Assert.Equal("Ready", afterReset!.Value.GetProperty("status").GetString());
            // Cleared modelUri is serialized as an omitted property (nulls are dropped), so
            // check for absence first — GetProperty would throw KeyNotFoundException.
            Assert.True(!afterReset.Value.TryGetProperty("modelUri", out var modelUriAfterReset)
                || modelUriAfterReset.ValueKind == JsonValueKind.Null);
        }
        finally { await DeleteDocAsync(docId); }
    }

    [Fact]
    public async Task CancelQueued_ReturnsReady()
    {
        var modelId = await GetTestModelIdAsync();
        var docId = await UploadSingleAsync("cancel-queued.docx");
        try
        {
            var jobId = await StartJobAsync(docId, modelId);
            var cancel = WithServer(HttpMethod.Post, $"{Base}/jobs/{jobId}/cancel");
            (await Client.SendAsync(cancel)).EnsureSuccessStatusCode();
            Assert.Equal("Ready", (await FindDocAsync(docId))!.Value.GetProperty("status").GetString());
        }
        finally { await DeleteDocAsync(docId); }
    }

    [Fact]
    public async Task CancelRunning_MakesPushConflict()
    {
        var modelId = await GetTestModelIdAsync();
        var docId = await UploadSingleAsync("cancel-running.docx");
        try
        {
            var jobId = await StartJobAsync(docId, modelId);
            var worker = WorkerClient();
            var lockToken = await PopUntilAsync(worker, jobId);

            var cancel = WithServer(HttpMethod.Post, $"{Base}/jobs/{jobId}/cancel");
            (await Client.SendAsync(cancel)).EnsureSuccessStatusCode();

            // The in-flight worker's push no-ops with 409.
            var push = await PushAsync(worker, jobId, lockToken, 0, "[]");
            Assert.Equal(HttpStatusCode.Conflict, push.StatusCode);

            Assert.Equal("Ready", (await FindDocAsync(docId))!.Value.GetProperty("status").GetString());
        }
        finally { await DeleteDocAsync(docId); }
    }

    [Fact]
    public async Task DeleteDocument_WorkerSeesGone()
    {
        var modelId = await GetTestModelIdAsync();
        var docId = await UploadSingleAsync("delete-running.docx");
        var jobId = await StartJobAsync(docId, modelId);
        var worker = WorkerClient();
        var lockToken = await PopUntilAsync(worker, jobId);

        // Deleting the document cascades away the running job.
        await DeleteDocAsync(docId);

        var docResp = await worker.GetAsync($"{Base}/worker/jobs/{jobId}/document?lockToken={lockToken}");
        Assert.Equal(HttpStatusCode.Gone, docResp.StatusCode);

        var push = await PushAsync(worker, jobId, lockToken, 0, "[]");
        Assert.Equal(HttpStatusCode.Gone, push.StatusCode);
    }

    // ---------------------------------------------------------------- worker auth

    [Fact]
    public async Task Worker_MissingApiKey_Unauthorized()
    {
        var anon = Fixture.CreateAnonymousClient();
        var resp = await anon.PostAsJsonAsync($"{Base}/worker/jobs/pop", new { workerId = "w1" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Worker_WrongApiKey_Unauthorized()
    {
        var c = Fixture.CreateAnonymousClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        var resp = await c.PostAsJsonAsync($"{Base}/worker/jobs/pop", new { workerId = "w1" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
