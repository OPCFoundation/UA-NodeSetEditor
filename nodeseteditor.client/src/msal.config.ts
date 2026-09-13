import { PublicClientApplication, LogLevel } from "@azure/msal-browser";

// A deployment without Azure AD (see runtimeConfig) still constructs an MSAL instance, because
// MsalProvider wraps the whole tree and useMsal() throws without it. PublicClientApplication
// rejects an empty client id at construction, so it gets a placeholder instead — nothing ever
// calls it, since the Microsoft sign-in button is not rendered and the axios interceptor skips
// token acquisition entirely.
const PLACEHOLDER_CLIENT_ID = '00000000-0000-0000-0000-000000000000';

export const msalConfig = {
   auth: {
      clientId: import.meta.env.VITE_MSAL_CLIENT_ID || PLACEHOLDER_CLIENT_ID,  // Application (client) id in Azure of the registered application
      authority: import.meta.env.VITE_MSAL_AUTHORITY || 'https://login.microsoftonline.com/common', // MSAL code will append client id, oauth path
      redirectUri: import.meta.env.VITE_REDIRECT_URL || '/login/success', // Must match Azure portal,
      postLogoutRedirectUri: '/', // Indicates the page to navigate after logout.
      navigateToLoginRequestUrl: true, // If "true", will navigate back to the original request location before processing the auth code response.
   },
   cache: {
      cacheLocation: "sessionStorage",
      storeAuthStateInCookie: false,
   },
   system: {
      loggerOptions: {
         // Only surface MSAL warnings and errors. Info / Verbose drown the
         // browser console with one-line entries on every token operation
         // ("@azure/msal-common: Info - 1sm769" etc.) which are useless
         // outside of an MSAL-specific debugging session.
         logLevel: LogLevel.Warning,
         loggerCallback: (level: LogLevel, message: string, containsPii: boolean) => {
            if (containsPii) return;
            switch (level) {
               case LogLevel.Error: console.error(message); return;
               case LogLevel.Warning: console.warn(message); return;
               default: return;
            }
         },
      },
   },
};

// Scopes requested at login. Including the API scope here means the access
// token is acquired alongside the ID token in the redirect round-trip and
// cached, so subsequent acquireTokenSilent calls hit the cache instead of
// triggering an iframe round-trip that often fails in modern browsers.
export const loginRequest = {
   scopes: [import.meta.env.VITE_MSAL_SCOPE],
   // Always show the Microsoft account picker instead of silently reusing the SSO session,
   // so the user can choose/switch accounts (or cancel back to the email-code option).
   prompt: 'select_account',
};

// The global MSAL instance.
export const msalInstance = new PublicClientApplication(msalConfig);