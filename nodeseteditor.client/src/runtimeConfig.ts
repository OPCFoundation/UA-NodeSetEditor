/**
 * Deployment shape, fetched from GET /api/config before the app renders.
 *
 * Which sign-in providers exist, whether test mode is on and whether the Cloud Library is
 * configured are properties of the *deployment*, not of the build — one container image serves
 * the hosted OPC Foundation instance and a self-hosted one with no Azure AD tenant. So the
 * client asks the server at startup rather than reading a build-time VITE_ variable.
 */
export interface RuntimeConfig {
   /** Sign-in paths the server accepts: always 'email', plus 'azuread' when configured. */
   authProviders: string[];
   /** True when the shared "Test Mode" account is available. */
   testMode: boolean;
   /** True when a Cloud Library URL is configured; the UI hides the feature otherwise. */
   cloudLibraryEnabled: boolean;
}

// Until /api/config answers, assume the most restricted shape: email-only sign-in, no test
// mode, no Cloud Library. A failed fetch must not conjure up a Microsoft button that cannot work.
const DEFAULT_CONFIG: RuntimeConfig = {
   authProviders: ['email'],
   testMode: false,
   cloudLibraryEnabled: false,
};

let current: RuntimeConfig = DEFAULT_CONFIG;

export function setRuntimeConfig(config: Partial<RuntimeConfig> | null | undefined): void {
   current = { ...DEFAULT_CONFIG, ...(config ?? {}) };
}

export function getRuntimeConfig(): RuntimeConfig {
   return current;
}

/** True when the deployment offers "Sign in with Microsoft". */
export function isAzureAdEnabled(): boolean {
   return current.authProviders.includes('azuread');
}

/**
 * Loads the configuration. Called once from the bootstrap in main.tsx, before any module that
 * depends on it is imported.
 */
export async function loadRuntimeConfig(): Promise<RuntimeConfig> {
   try {
      const base = import.meta.env.VITE_BASE_API_URL ?? '/api';
      const response = await fetch(`${base}/config`, { credentials: 'same-origin' });
      if (response.ok) {
         setRuntimeConfig(await response.json());
      } else {
         console.error(`GET ${base}/config → ${response.status}; using restricted defaults`);
      }
   } catch (error) {
      console.error('Could not load runtime configuration; using restricted defaults', error);
   }
   return current;
}
