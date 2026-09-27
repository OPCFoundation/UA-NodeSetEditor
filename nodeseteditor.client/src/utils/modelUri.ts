/**
 * Namespace-URI helpers for the create-model dialog. Extracted from ModelLibraryPage so the
 * CSV import flow can offer the same dialog without duplicating the rules.
 */

/**
 * Strip characters that can't appear unescaped in a URN namespace-specific string. We keep
 * alphanumerics plus a small set of safe punctuation (- . _ ~) and replace runs of
 * whitespace/other chars with a single dash so "My Model 1.0!" becomes "My-Model-1.0".
 */
export function sanitizeUriSegment(value: string): string {
   if (!value) return '';
   return value
      .trim()
      .replace(/[^A-Za-z0-9._~-]+/g, '-')
      .replace(/^-+|-+$/g, '');
}

/**
 * Validate a model namespace URI for the create dialog. Mirrors the server's import-time rule
 * (CanonicalUri.IsValid): absolute, ASCII-only, scheme restricted to http/https/urn,
 * well-formed percent-encoding (valid %HH accepted, bare/truncated "%" rejected), and no ";"
 * — then adds the URN Namespace Identifier (NID) check that import intentionally omits. The
 * editor never adds percent-encoding; URIs are expected to arrive already correctly encoded,
 * and we only reject under-encoding. Returns an error message, or null if valid.
 */
export function validateModelUri(uri: string): string | null {
   const value = uri.trim();
   if (!value) return 'A namespace URI is required.';
   if (/\s/.test(value)) return 'Namespace URI must not contain whitespace.';
   // A "%" must be followed by two hex digits; a bare or truncated escape is under-encoded.
   if (/%(?![0-9A-Fa-f]{2})/.test(value)) return 'Namespace URI has malformed percent-encoding (use %HH).';
   if (value.includes(';')) return 'Namespace URI must not contain ";".';
   for (const ch of value) {
      const c = ch.codePointAt(0)!;
      if (c < 0x20 || c === 0x7f) return 'Namespace URI must not contain control characters.';
      if (c > 0x7e) return 'Namespace URI must contain ASCII characters only.';
   }

   const schemeMatch = value.match(/^([a-zA-Z][a-zA-Z0-9+.-]*):/);
   if (!schemeMatch) {
      return 'Namespace URI must be absolute (start with http://, https://, or urn:).';
   }
   const scheme = schemeMatch[1].toLowerCase();
   if (scheme !== 'http' && scheme !== 'https' && scheme !== 'urn') {
      return 'Namespace URI must use the http, https, or urn scheme.';
   }

   if (scheme === 'http' || scheme === 'https') {
      try {
         const url = new URL(value);
         if (!url.hostname) return 'Namespace URI must include a host name.';
      } catch {
         return 'Namespace URI is not a valid URL.';
      }
      return null;
   }

   // urn:<NID>:<NSS>
   const urnMatch = value.match(/^urn:([^:]*):(.*)$/i);
   if (!urnMatch || !urnMatch[1]) {
      return 'A URN must have the form urn:<identifier>:<value>.';
   }
   const nid = urnMatch[1];
   const nss = urnMatch[2];
   if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,30}[A-Za-z0-9]$/.test(nid)) {
      return `Invalid URN identifier "${nid}": use 2–32 letters, digits, or hyphens ` +
         `with no dots or underscores (e.g. "shaleriver-com").`;
   }
   if (!nss) {
      return 'A URN must include text after the identifier (urn:<identifier>:<value>).';
   }
   return null;
}

/**
 * Build everything before the final "<model name>" part, of the form
 * "urn:opcua:<domain>:<YYYY-MM>:". The fixed "opcua" is the URN Namespace Identifier (NID);
 * the domain lives in the namespace-specific string where dots are allowed, so it is kept
 * verbatim (only stripped of characters that would need encoding). The domain is the user's
 * DefaultDomain preference when set, otherwise the email domain. If neither yields a usable
 * domain we return null so the caller can fall back to a blank URI rather than emit
 * "urn:opcua::2026-04:".
 */
export function computeModelUriPrefix(email: string, now: Date, defaultDomain?: string): string | null {
   let domainSource = defaultDomain?.trim();
   if (!domainSource) {
      const at = email.indexOf('@');
      if (at <= 0 || at === email.length - 1) return null;
      domainSource = email.slice(at + 1);
   }
   const domain = sanitizeUriSegment(domainSource);
   if (!domain) return null;
   const yyyy = now.getFullYear().toString().padStart(4, '0');
   const mm = (now.getMonth() + 1).toString().padStart(2, '0');
   return `urn:opcua:${domain}:${yyyy}-${mm}:`;
}
