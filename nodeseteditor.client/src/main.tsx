import * as React from 'react';

import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom';
import { ErrorBoundary } from 'react-error-boundary'

import { EventType, type EventMessage, type AuthenticationResult } from '@azure/msal-browser';
import { MsalProvider } from '@azure/msal-react';

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import CssBaseline from '@mui/material/CssBaseline';

import { loadRuntimeConfig, isAzureAdEnabled } from './runtimeConfig';

import './index.css'

// 1. Define the QueryClient instance for @tanstack
const queryClient = new QueryClient({
   defaultOptions: {
      queries: {
         // Best practice: set a staleTime so it doesn't refetch constantly
         staleTime: 1000 * 60 * 5, // 5 minutes
         retry: 1, // Only retry failed requests once
      },
   },
});

// 2. Bootstrap.
//
// The runtime configuration is fetched FIRST, and everything that depends on it is imported
// dynamically afterwards. Module bodies run at import time, so a static import of msal.config
// (or of anything that pulls it in, such as the axios client) would evaluate before the fetch
// resolved and would read the wrong configuration.
//
// MSAL Browser v3+ requires initialize() to be awaited before any other API call — including
// MsalProvider's mount, which internally invokes handleRedirectPromise. Account selection and
// the LOGIN_SUCCESS callback must also be wired after initialize() resolves.
async function bootstrap() {
   await loadRuntimeConfig();

   const { msalInstance } = await import('./msal.config');
   const [{ UserProvider }, { WorkspaceProvider }, { HelpProvider }, ErrorFallback, App] = await Promise.all([
      import('./UserProvider'),
      import('./WorkspaceProvider'),
      import('./HelpContext'),
      import('./pages/ErrorFallback').then(m => m.default),
      import('./App.tsx').then(m => m.default),
      import('./i18n'),
   ]);

   await msalInstance.initialize();

   // A deployment without Azure AD still mounts MsalProvider (useMsal() requires it) but never
   // drives it, so the account plumbing below is skipped along with the redirect handling.
   if (isAzureAdEnabled()) {
      // Default to using the first account if no account is active on page load
      const accounts = msalInstance.getAllAccounts();
      if (accounts?.length > 0) {
         msalInstance.setActiveAccount(accounts[0]);
      }

      // Listen for sign-in event and set active account
      msalInstance.addEventCallback((event: EventMessage) => {
         if (event.eventType === EventType.LOGIN_SUCCESS && event.payload) {
            const payload = event.payload as AuthenticationResult;
            if (payload.account) {
               msalInstance.setActiveAccount(payload.account);
            }
         }
      });
   }

   createRoot(document.getElementById('root')!).render(
      <React.StrictMode>
         <MsalProvider instance={msalInstance}>
            <QueryClientProvider client={queryClient}>
               <UserProvider>
                  <WorkspaceProvider>
                     <BrowserRouter>
                        <HelpProvider>
                           <CssBaseline />
                           <ErrorBoundary FallbackComponent={ErrorFallback}>
                              <App />
                           </ErrorBoundary>
                        </HelpProvider>
                     </BrowserRouter>
                  </WorkspaceProvider>
               </UserProvider>
            </QueryClientProvider>
         </MsalProvider>
      </React.StrictMode>,
   );
}

bootstrap();
