/**
 * Encode an OPC UA NodeId (or any string with arbitrary Unicode content) as a
 * URL-path-safe base64url slug per RFC 4648 §5.
 *
 * Why: NodeIds routinely contain `:`, `;`, `=`, `/`, and non-ASCII characters.
 * Percent-encoding works in theory but loses constantly to upstream URL
 * filtering (IIS/App Service rejects `%2F`, some proxies eat `;` as a path
 * parameter, multi-lingual NodeIds need careful UTF-8 handling). base64url
 * uses only `A-Z a-z 0-9 - _` — characters no proxy ever filters — so the
 * resulting URL passes through untouched to ASP.NET Core's routing.
 *
 * Always use this helper when embedding a NodeId in an API URL path.
 * `encodeURIComponent` should NOT be called on NodeIds directly.
 */
export function slugifyNodeId(nodeId: string): string {
   const bytes = new TextEncoder().encode(nodeId);
   // btoa wants a binary string; build one byte-by-byte (bytes are < 256)
   let bin = '';
   for (let i = 0; i < bytes.length; i++) {
      bin += String.fromCharCode(bytes[i]);
   }
   return btoa(bin)
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '');
}

/**
 * Inverse of {@link slugifyNodeId}. Mostly useful for tests / logging — the
 * server is the primary decoder.
 */
export function unslugifyNodeId(slug: string): string {
   const padded = slug.replace(/-/g, '+').replace(/_/g, '/')
      + '='.repeat((4 - (slug.length % 4)) % 4);
   const bin = atob(padded);
   const bytes = new Uint8Array(bin.length);
   for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
   return new TextDecoder().decode(bytes);
}
