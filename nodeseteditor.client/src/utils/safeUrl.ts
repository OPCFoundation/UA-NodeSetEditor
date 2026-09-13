const OPC_FOUNDATION_HOST = 'opcfoundation.org';

/**
 * Returns the URL verbatim if it is a valid HTTPS URL on the opcfoundation.org
 * domain (or a subdomain). Returns null for anything else — malformed URLs,
 * non-HTTPS schemes, or off-domain targets are all silently rejected so that
 * a stale translation key or misconfigured constant can never open an
 * unexpected external site.
 */
export function safeOpcFoundationUrl(url: string): string | null {
   try {
      const parsed = new URL(url);
      if (parsed.protocol !== 'https:') return null;
      if (
         parsed.hostname !== OPC_FOUNDATION_HOST &&
         !parsed.hostname.endsWith('.' + OPC_FOUNDATION_HOST)
      ) return null;
      return parsed.href;
   } catch {
      return null;
   }
}
