
import axios from 'axios';
import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { msalInstance } from '../msal.config';
import { isAzureAdEnabled } from '../runtimeConfig';

export class ApiResult {
   public readonly errorCode?: string | number | null;
   public readonly errorMessage?: string | number | null;

   constructor(errorMessage?: string | null, errorCode?: string | number | null) {
      this.errorCode = errorCode;
      this.errorMessage = errorMessage;

      // Fix the prototype chain (Essential for 'instanceof' to work in TS/ES5)
      Object.setPrototypeOf(this, ApiResult.prototype);
   }
}

// Pull the human-readable reason out of an error response body. The API returns several
// shapes: the OPC ErrorResponse ({ statusCode, message }), the ad-hoc { error } used by the
// Cloud Library endpoints, the legacy { errorMessage }, ASP.NET's ProblemDetails
// ({ title, detail }) and its ValidationProblemDetails ({ errors }) for model-binding
// failures, and occasionally a bare string. Anything else yields null so the caller keeps
// whatever message it already had.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function serverErrorMessage(data: any): string | null {
   if (!data) return null;
   if (typeof data === 'string') return data.trim() || null;
   // Blob bodies (responseType: 'blob') can't be read synchronously — callers that ask for
   // blobs handle their own error decoding.
   if (typeof Blob !== 'undefined' && data instanceof Blob) return null;

   for (const key of ['message', 'error', 'errorMessage', 'detail', 'title']) {
      const v = data[key];
      if (typeof v === 'string' && v.trim()) return v.trim();
   }

   // ValidationProblemDetails: { errors: { Field: ["reason", ...] } }
   if (data.errors && typeof data.errors === 'object') {
      const flattened = Object.values(data.errors)
         .flatMap((v) => (Array.isArray(v) ? v : [v]))
         .filter((v): v is string => typeof v === 'string' && v.trim().length > 0);
      if (flattened.length > 0) return flattened.join('\n');
   }

   return null;
}

export function extractErrorMessage(e: unknown, fallback: string): string {
   if (e instanceof ApiError) return e.message;
   if (axios.isAxiosError(e)) {
      return serverErrorMessage(e.response?.data) ?? e.message;
   }
   if (e instanceof Error) return e.message;
   return fallback;
}

export class ApiError extends Error {
   public readonly code: string | number;

   constructor(message: string, code: string | number) {
      // Pass message to the base Error class
      super(message);

      // Set the name to the class name instead of "Error"
      this.name = this.constructor.name;

      // Assign the custom code
      this.code = code;

      // Fix the prototype chain (Essential for 'instanceof' to work in TS/ES5)
      Object.setPrototypeOf(this, ApiError.prototype);

      // Cast to 'any' or check existence to satisfy
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const stackSource: any = Error;
      if (stackSource.captureStackTrace) {
         stackSource.captureStackTrace(this, ApiError);
      }
   }

   // Static helper to evaluate an object and throw if error data is present
   static checkForError(data: { errorCode?: string | number | null; errorMessage?: string | null }) {
      if (data.errorCode || data.errorMessage) {
         throw new ApiError(
            data.errorMessage ?? 'An unknown error occurred',
            data.errorCode ?? 'BadUnknownError'
         );
      }
   }
}

const DEV_AUTH = import.meta.env.VITE_DEV_AUTH === 'true';

const api = axios.create({
   baseURL: import.meta.env.VITE_BASE_API_URL,
   // Send the HttpOnly email-code session cookie (opc-email-auth) on API calls.
   // Harmless for the Azure AD path, which authenticates via the Bearer header instead.
   withCredentials: true,
});

api.interceptors.request.use(async (config) => {
   // In dev auth mode, skip MSAL token acquisition entirely.
   // The server's DevAuth middleware will inject fake claims.
   if (DEV_AUTH) {
      config.headers['X-Dev-Auth'] = 'true';
      return config;
   }

   // No Azure AD in this deployment: the only credential is the email-code session cookie,
   // which withCredentials already sends. The MSAL instance here is a placeholder that must
   // never be driven, so skip token acquisition entirely.
   if (!isAzureAdEnabled()) {
      return config;
   }

   // Ensure MSAL is initialized (idempotent — safe to call multiple times).
   // This guards against a race where an API call fires before MsalProvider
   // finishes async initialization in @azure/msal-browser v5.
   await msalInstance.initialize();

   // Ensure any pending redirect response has been processed before reading
   // the accounts list. Without this, a request that fires immediately after
   // the post-login page load can run before MsalProvider's own
   // handleRedirectPromise resolves — getAllAccounts() returns [] and the
   // request would go out anonymous. handleRedirectPromise is idempotent
   // and resolves to the cached result on subsequent calls.
   await msalInstance.handleRedirectPromise();

   const accounts = msalInstance.getAllAccounts();
   if (accounts.length > 0) {
      const tokenRequest = {
         scopes: [import.meta.env.VITE_MSAL_SCOPE],
         account: accounts[0],
      };
      try {
         const authResult = await msalInstance.acquireTokenSilent(tokenRequest);
         config.headers.Authorization = `Bearer ${authResult.accessToken}`;
      } catch (error) {
         if (error instanceof InteractionRequiredAuthError) {
            // Silent acquisition failed (e.g. consent required, token expired
            // beyond silent renewal). Fall back to an interactive popup.
            try {
               const authResult = await msalInstance.acquireTokenPopup(tokenRequest);
               config.headers.Authorization = `Bearer ${authResult.accessToken}`;
            } catch (popupError) {
               // Surface the failure to the caller (React Query) so the UI
               // shows an error instead of silently issuing an unauthenticated
               // request that returns 401.
               console.error("Interactive token acquisition failed", popupError);
               throw popupError;
            }
         } else {
            console.error("Token acquisition failed", error);
            throw error;
         }
      }
   }
   return config;
});

api.interceptors.response.use(
   (response) => response,
   (error) => {
      if (axios.isAxiosError(error)) {
         const { config, response } = error;
         console.error(
            `API ${config?.method?.toUpperCase()} ${config?.url} → ${response?.status ?? 'no response'}`,
            response?.data ?? error.message
         );

         // Axios sets message to "Request failed with status code 400", and most catch sites
         // surface exactly that — so a server that carefully explained itself ("Cannot remove
         // model 'x' / It is used by / y") showed the user nothing but the status code. Promote
         // the server's explanation onto the error here, in the one place every request passes
         // through, so both the extractErrorMessage() helper and the hand-rolled
         // `e instanceof Error ? e.message` catch sites report the real reason.
         //
         // The error stays an AxiosError: callers that inspect response.status or a Blob body
         // (ViewNodeDialog's 404 check, the model export) keep working.
         const serverMessage = serverErrorMessage(response?.data);
         if (serverMessage) error.message = serverMessage;
      }
      return Promise.reject(error);
   }
);

export default api;