const KEY = 'opcua-editor:lastCreateModelUri';

export function getLastModelUri(): string {
   try { return localStorage.getItem(KEY) ?? ''; } catch { return ''; }
}

export function setLastModelUri(uri: string): void {
   try { localStorage.setItem(KEY, uri); } catch { /* ignore */ }
}
