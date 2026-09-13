import { OPC_UA_CORE_URI } from './formatNodeId';

const NODE_ID_PREFIXES = ['i', 's', 'g', 'b'] as const;
export type NodeIdPrefix = typeof NODE_ID_PREFIXES[number];
export { NODE_ID_PREFIXES };

// Numeric identifiers are UInt32 (Part 6 §5.2.2.9).
const MAX_NUMERIC_IDENTIFIER = 4294967295;

export function validateNodeIdValue(prefix: NodeIdPrefix, value: string): string | null {
   if (!value) return 'Value is required.';
   switch (prefix) {
      case 'i':
         if (!/^\d+$/.test(value)) return 'Must be a non-negative integer.';
         return Number(value) <= MAX_NUMERIC_IDENTIFIER
            ? null : `Must be at most ${MAX_NUMERIC_IDENTIFIER}.`;
      case 's':
         return value.trim() ? null : 'Must not be empty.';
      case 'g':
         return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
            ? null : 'Must be a valid GUID.';
      case 'b':
         try { atob(value); return null; } catch { return 'Must be valid Base64.'; }
      default:
         return null;
   }
}

/** The namespace fields needed to resolve a "[Model]:" or "ns=<n>;" prefix. */
export interface NamespaceRef {
   index?: number;
   uri?: string;
   name?: string;
}

export interface ParsedNodeId {
   /**
    * Canonical NodeId, ready to send to the API: "nsu=<uri>;i=1234", or the
    * bare "i=1234" form for the core namespace (which is how core nodes are
    * keyed in the address space).
    */
   nodeId: string;
   namespaceUri: string;
   idType: NodeIdPrefix;
   identifier: string;
}

export type ParseNodeIdResult =
   | { ok: true; value: ParsedNodeId }
   | { ok: false; error: string };

// "[ModelName]:<rest>" — the display form produced by formatNodeId. The model
// name never contains ']', so the first one closes the prefix; everything after
// the following ':' is the NodeId body (which may itself contain ':' and ']',
// e.g. a string identifier).
const MODEL_PREFIX_RE = /^\[([^\]]*)\]\s*:\s*([\s\S]*)$/;

/**
 * Mirrors CanonicalUri.IsValid on the server: ASCII only, no ';' (it separates
 * the URI from the identifier), well-formed percent-encoding, http/https/urn.
 */
function invalidUriReason(uri: string): string | null {
   if (!uri) return 'the namespace URI is empty';
   if (uri.includes(';')) return '";" is not allowed inside a namespace URI';
   if (/%(?![0-9a-fA-F]{2})/.test(uri)) return 'its percent-encoding is malformed';
   if (/[^\x20-\x7e]/.test(uri)) return 'it must contain only printable ASCII';
   if (!/^(https?|urn):/i.test(uri)) return 'its scheme must be http, https or urn';
   return null;
}

/**
 * Parse a NodeId typed by hand into its canonical form.
 *
 * Accepted input:
 *   i=1234 / s=Foo / g=<guid> / b=<base64>   → core namespace
 *   nsu=<uri>;<id>                           → namespace by URI
 *   ns=<index>;<id>                          → namespace by index (resolved via `namespaces`)
 *   [ModelName]:<id>                         → the display form shown elsewhere in the UI
 *
 * The "[ModelName]:" prefix is resolved against the workspace's namespaces and
 * rewritten to "nsu=<uri>;" — the UI shows NodeIds that way (see formatNodeId),
 * so users copy them back in that form.
 */
export function parseNodeId(input: string, namespaces: NamespaceRef[] = []): ParseNodeIdResult {
   const text = (input ?? '').trim();
   if (!text) return { ok: false, error: 'A NodeId is required.' };

   let rest = text;
   let uri: string | undefined;

   const modelMatch = MODEL_PREFIX_RE.exec(rest);
   if (modelMatch) {
      const name = modelMatch[1].trim();
      rest = modelMatch[2].trim();
      if (!name) return { ok: false, error: 'The model name in "[…]:" is empty.' };

      // Accept either the model name (what the UI displays) or the namespace
      // URI itself between the brackets.
      const byName = namespaces.filter(
         (ns) => !!ns.uri && (ns.name?.toLowerCase() === name.toLowerCase() || ns.uri === name));
      const uris = new Set(byName.map((ns) => ns.uri!));
      if (uris.size === 0) {
         return { ok: false, error: `"${name}" is not a model in this workspace.` };
      }
      if (uris.size > 1) {
         return { ok: false, error: `"${name}" matches more than one model — use the nsu=<uri>; form.` };
      }
      uri = byName[0].uri;

      if (/^(nsu=|ns=|svr=)/.test(rest)) {
         return { ok: false, error: 'Use either the "[Model]:" prefix or "nsu=…;" — not both.' };
      }
   } else if (rest.startsWith('svr=')) {
      return { ok: false, error: 'Server-index NodeIds ("svr=…") cannot be used here.' };
   } else if (rest.startsWith('nsu=')) {
      const semi = rest.indexOf(';', 4);
      if (semi < 0) {
         return { ok: false, error: 'Missing ";" after the namespace URI (expected nsu=<uri>;i=1234).' };
      }
      uri = rest.substring(4, semi);
      rest = rest.substring(semi + 1);
      const reason = invalidUriReason(uri);
      if (reason) return { ok: false, error: `Invalid namespace URI: ${reason}.` };
   } else if (rest.startsWith('ns=')) {
      const semi = rest.indexOf(';', 3);
      if (semi < 0) {
         return { ok: false, error: 'Missing ";" after the namespace index (expected ns=1;i=1234).' };
      }
      const indexText = rest.substring(3, semi);
      rest = rest.substring(semi + 1);
      if (!/^\d+$/.test(indexText)) {
         return { ok: false, error: 'The namespace index must be a non-negative integer.' };
      }
      const index = Number(indexText);
      const match = namespaces.find((ns) => ns.index === index && !!ns.uri);
      if (!match) {
         return { ok: false, error: `Namespace index ${index} is not in this workspace.` };
      }
      uri = match.uri;
   }

   const idMatch = /^([isgb])=([\s\S]*)$/.exec(rest);
   if (!idMatch) {
      return { ok: false, error: 'Missing identifier type — expected i=, s=, g= or b= (e.g. i=1234).' };
   }
   const idType = idMatch[1] as NodeIdPrefix;
   const identifier = idMatch[2];

   const valueError = validateNodeIdValue(idType, identifier);
   if (valueError) return { ok: false, error: `Invalid ${idType}= identifier: ${valueError}` };

   const namespaceUri = uri ?? OPC_UA_CORE_URI;
   const idPart = `${idType}=${identifier}`;
   return {
      ok: true,
      value: {
         // Core-namespace nodes are keyed without the nsu= prefix.
         nodeId: namespaceUri === OPC_UA_CORE_URI ? idPart : `nsu=${namespaceUri};${idPart}`,
         namespaceUri,
         idType,
         identifier,
      },
   };
}
