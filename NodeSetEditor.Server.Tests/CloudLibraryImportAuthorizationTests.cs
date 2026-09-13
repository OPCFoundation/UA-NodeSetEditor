using System.Net;
using System.Net.Http.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Guards the owner-only write gate on the Cloud Library import endpoint.
/// Import mutates the target workspace (links/replaces models and rewrites
/// metadata), so ACL collaborators — who are read-only everywhere else — must
/// get 403 and non-members 404. Regression test for an authorization gap where
/// any accessible workspace (including shared-in ones) could be mutated via
/// import.
/// </summary>
[Collection("Api")]
public class CloudLibraryImportAuthorizationTests : UaRestTestBase
{
    private const string CollaboratorId = "import-collab-001";
    private const string CollaboratorEmail = "import-collab@test.net";
    private const string StrangerId = "import-stranger-001";
    private const string StrangerEmail = "import-stranger@test.net";

    // Non-numeric, so it can never resolve to a real Cloud Library model even
    // when the test environment has Cloud Library credentials configured.
    private const string BogusIdentifier = "not-a-real-identifier";

    public CloudLibraryImportAuthorizationTests(ApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Import_RequiresWorkspaceOwnership()
    {
        try
        {
            await SetAcl(new[] { CollaboratorEmail });

            // An ACL collaborator has read-only access: import must be
            // rejected before any mutation (or Cloud Library call) happens.
            var collabReq = AsUser(HttpMethod.Post,
                $"/api/opcua/v1/cloudlibrary/import/{BogusIdentifier}",
                CollaboratorId, CollaboratorEmail);
            collabReq.Headers.Add("OpcUa-Server", WorkspaceUrn);
            var collabResp = await Client.SendAsync(collabReq);
            Assert.Equal(HttpStatusCode.Forbidden, collabResp.StatusCode);

            // A user with no access at all must not learn the workspace exists.
            var strangerReq = AsUser(HttpMethod.Post,
                $"/api/opcua/v1/cloudlibrary/import/{BogusIdentifier}",
                StrangerId, StrangerEmail);
            strangerReq.Headers.Add("OpcUa-Server", WorkspaceUrn);
            var strangerResp = await Client.SendAsync(strangerReq);
            Assert.Equal(HttpStatusCode.NotFound, strangerResp.StatusCode);

            // Sanity: the owner passes the gate. The request may still fail
            // later (unconfigured Cloud Library or unknown identifier) but
            // never with an authorization status.
            var ownerReq = WithServer(HttpMethod.Post,
                $"/api/opcua/v1/cloudlibrary/import/{BogusIdentifier}");
            var ownerResp = await Client.SendAsync(ownerReq);
            Assert.NotEqual(HttpStatusCode.Unauthorized, ownerResp.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, ownerResp.StatusCode);
        }
        finally
        {
            // Restore the shared fixture workspace to its unshared state.
            await SetAcl(Array.Empty<string>());
        }
    }

    private async Task SetAcl(string[] emails)
    {
        var req = WithServer(HttpMethod.Put,
            $"/api/opcua/v1/servers/{Uri.EscapeDataString(WorkspaceUrn)}");
        req.Content = JsonContent.Create(new { acl = emails });
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }
}
