const KEY = 'opcua-editor:recentTypes';

/**
 * Which dropdown a selection came from. Each context keeps its own list so that, say,
 * editing a run of DataTypes does not push the user's recent TypeDefinitions out of
 * the window — the lists are short, and a shared one would thrash.
 */
export type RecentTypeContext = 'dataType' | 'superType' | 'typeDefinition' | 'referenceType';

/**
 * How many ids we keep per context. Deliberately larger than the number we show
 * (RECENT_SHOWN): the stored ids are filtered against whatever the dropdown actually
 * offers, so a list of exactly 10 would often surface far fewer than 10.
 */
const RECENT_STORED = 20;

/** Most we put in the "frequently used" group of a single dropdown. */
export const RECENT_SHOWN = 10;

/**
 * NodeIds are only meaningful within one workspace — the namespace index in `ns=2;i=5`
 * is resolved per workspace — so the lists are kept per workspace as well as per context.
 */
type RecentStore = Record<string, Partial<Record<RecentTypeContext, string[]>>>;

function read(): RecentStore {
   try {
      const raw = localStorage.getItem(KEY);
      if (!raw) return {};
      const parsed: unknown = JSON.parse(raw);
      // Written by an older build or hand-edited — treat anything unexpected as empty
      // rather than letting it break every dropdown in the app.
      return (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) ? parsed as RecentStore : {};
   } catch {
      return {};
   }
}

/** Most-recently-selected ids for this context, newest first. */
export function getRecentTypes(context: RecentTypeContext, workspaceId: string): string[] {
   if (!workspaceId) return [];
   const list = read()[workspaceId]?.[context];
   return Array.isArray(list) ? list.filter((id) => typeof id === 'string') : [];
}

/** Moves `nodeId` to the front of its list, dropping the oldest beyond RECENT_STORED. */
export function recordRecentType(context: RecentTypeContext, workspaceId: string, nodeId: string): void {
   if (!workspaceId || !nodeId) return;
   try {
      const store = read();
      const existing = getRecentTypes(context, workspaceId);
      const next = [nodeId, ...existing.filter((id) => id !== nodeId)].slice(0, RECENT_STORED);
      store[workspaceId] = { ...store[workspaceId], [context]: next };
      localStorage.setItem(KEY, JSON.stringify(store));
   } catch {
      /* private mode, or the quota is full — the lists are a convenience, not state we own */
   }
}
