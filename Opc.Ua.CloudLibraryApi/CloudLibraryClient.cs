using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Opc.Ua.CloudLibraryApi
{
    /// <summary>
    /// Typed HTTP client for the OPC UA Cloud Library InfoModel API.
    /// See https://uacloudlibrary.opcfoundation.org
    /// </summary>
    public class CloudLibraryClient
    {
        private readonly HttpClient _http;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public CloudLibraryClient(HttpClient httpClient)
        {
            _http = httpClient;
        }

        /// <summary>
        /// Sets Basic authentication credentials on the underlying HttpClient.
        /// </summary>
        public void SetBasicAuth(string username, string password)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        /// <summary>
        /// Search for nodesets by keywords.
        /// GET /infomodel/find?keywords={kw}&amp;offset={offset}&amp;limit={limit}
        /// </summary>
        public async Task<UANodesetResult[]?> FindAsync(
            string[] keywords, int offset = 0, int limit = 100,
            CancellationToken ct = default)
        {
            var sb = new StringBuilder("infomodel/find?");
            foreach (var kw in keywords)
                sb.Append($"keywords={Uri.EscapeDataString(kw)}&");
            sb.Append($"offset={offset}&limit={limit}");

            var resp = await _http.GetAsync(sb.ToString(), ct);
            resp.EnsureSuccessStatusCode();
            return await DeserializeAsync<UANodesetResult[]>(resp, ct);
        }

        /// <summary>
        /// Search for nodesets by keywords and optional namespace URI.
        /// GET /infomodel/find2
        ///
        /// Returns UANodesetResult (not just UANameSpace) because the cloud
        /// library puts the namespace URI / version / publication date at the
        /// top level of each result rather than under a nested "nodeset"
        /// object. Deserializing as the parent type silently dropped those
        /// fields and broke downstream consumers that grouped by namespace.
        /// </summary>
        public async Task<UANodesetResult[]?> Find2Async(
            string[]? keywords = null, string? namespaceUri = null,
            int offset = 0, int limit = 100,
            CancellationToken ct = default)
        {
            var sb = new StringBuilder($"infomodel/find2?offset={offset}&limit={limit}");
            if (keywords != null)
            {
                foreach (var kw in keywords)
                    sb.Append($"&keywords={Uri.EscapeDataString(kw)}");
            }
            if (namespaceUri != null)
                sb.Append($"&namespaceUri={Uri.EscapeDataString(namespaceUri)}");

            var resp = await _http.GetAsync(sb.ToString(), ct);
            resp.EnsureSuccessStatusCode();
            return await DeserializeAsync<UANodesetResult[]>(resp, ct);
        }

        /// <summary>
        /// List all namespace URIs in the Cloud Library.
        /// GET /infomodel/namespaces
        /// </summary>
        public async Task<string[]?> GetNamespacesAsync(CancellationToken ct = default)
        {
            var resp = await _http.GetAsync("infomodel/namespaces", ct);
            resp.EnsureSuccessStatusCode();
            return await DeserializeAsync<string[]>(resp, ct);
        }

        /// <summary>
        /// List all model names in the Cloud Library.
        /// GET /infomodel/names
        /// </summary>
        public async Task<string[]?> GetNamesAsync(CancellationToken ct = default)
        {
            var resp = await _http.GetAsync("infomodel/names", ct);
            resp.EnsureSuccessStatusCode();
            return await DeserializeAsync<string[]>(resp, ct);
        }

        /// <summary>
        /// Get types defined in a model.
        /// GET /infomodel/types/{identifier}
        /// </summary>
        public async Task<string[]?> GetTypesAsync(string identifier, CancellationToken ct = default)
        {
            var resp = await _http.GetAsync($"infomodel/types/{Uri.EscapeDataString(identifier)}", ct);
            resp.EnsureSuccessStatusCode();
            return await DeserializeAsync<string[]>(resp, ct);
        }

        /// <summary>
        /// Get instances defined in a model.
        /// GET /infomodel/instances/{identifier}
        /// </summary>
        public async Task<string[]?> GetInstancesAsync(string identifier, CancellationToken ct = default)
        {
            var resp = await _http.GetAsync($"infomodel/instances/{Uri.EscapeDataString(identifier)}", ct);
            resp.EnsureSuccessStatusCode();
            return await DeserializeAsync<string[]>(resp, ct);
        }

        /// <summary>
        /// Download a model and/or its metadata.
        /// GET /infomodel/download/{identifier}
        /// </summary>
        public async Task<UANameSpace?> DownloadAsync(
            string identifier,
            bool nodesetXmlOnly = false,
            bool metadataOnly = false,
            CancellationToken ct = default)
        {
            var url = $"infomodel/download/{Uri.EscapeDataString(identifier)}?nodesetXMLOnly={nodesetXmlOnly}&metadataOnly={metadataOnly}";
            var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            return await DeserializeAsync<UANameSpace>(resp, ct);
        }

        /// <summary>
        /// Upload a model.
        /// PUT /infomodel/upload
        /// </summary>
        public async Task<string?> UploadAsync(
            UANameSpace model, bool overwrite = false,
            CancellationToken ct = default)
        {
            var url = $"infomodel/upload?overwrite={overwrite}";
            var json = JsonSerializer.Serialize(model, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// Delete a model.
        /// DELETE /infomodel/delete/{identifier}
        /// </summary>
        public async Task<UANameSpace?> DeleteAsync(string identifier, CancellationToken ct = default)
        {
            var resp = await _http.DeleteAsync($"infomodel/delete/{Uri.EscapeDataString(identifier)}", ct);
            resp.EnsureSuccessStatusCode();
            return await DeserializeAsync<UANameSpace>(resp, ct);
        }

        private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage resp, CancellationToken ct)
        {
#if NETSTANDARD2_0
            var json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
#else
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct);
#endif
        }
    }
}
