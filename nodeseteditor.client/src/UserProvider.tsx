import * as React from 'react';

import { useMsal } from '@azure/msal-react';
import { EventType, InteractionStatus, type EventMessage } from '@azure/msal-browser';
import Chip from '@mui/material/Chip';
import { ThemeProvider } from '@mui/material/styles';
import { ThemeModes, LightTheme, DarkTheme } from './theme';

import { UserContext, DefaultUserName, type UserContextType } from './UserContext';
import { loginRequest } from './msal.config';
import { getRuntimeConfig, isAzureAdEnabled } from './runtimeConfig';
import type { ThemeMode } from './theme';
import api from './api/axios.api';
import TermsConsentDialog from './components/TermsConsentDialog';

const DEV_AUTH = import.meta.env.VITE_DEV_AUTH === 'true';

// Pull a safe, displayable message out of an axios error from the /auth endpoints.
// The server returns { message } bodies that are intentionally generic (no enumeration).
function extractAuthMessage(error: unknown, fallback: string): string {
   const data = (error as { response?: { data?: { message?: string } } } | null)?.response?.data;
   return data?.message ?? fallback;
}

function describeAuthError(error: unknown): { message: string; code: string | null } {
   // MSAL BrowserAuthError: interaction already in progress (stale session state)
   const browserCode = (error as { errorCode?: string } | null)?.errorCode;
   if (browserCode === 'interaction_in_progress') {
      return {
         message: 'Sign-in state is stale — clearing and retrying. If this keeps happening, refresh the page.',
         code: browserCode,
      };
   }

   const raw = (error as { errorMessage?: string; message?: string } | null);
   const text = raw?.errorMessage ?? raw?.message ?? String(error ?? '');
   const codeMatch = text.match(/AADSTS\d+/);
   const code = codeMatch ? codeMatch[0] : null;

   switch (code) {
      case 'AADSTS50020':
      case 'AADSTS50105':
      case 'AADSTS65001':
      case 'AADSTS90072':
         return {
            message: 'Sign-in was blocked by your Microsoft tenant administrator. '
               + 'Contact your IT department or try a personal Microsoft account.',
            code,
         };
      case 'AADSTS50059':
      case 'AADSTS500011':
         return {
            message: 'Microsoft could not identify your tenant. Try signing in again.',
            code,
         };
      default:
         return {
            message: code
               ? `Sign-in failed (${code}). Try again or use a different Microsoft account.`
               : 'Sign-in failed. Try again or use a different Microsoft account.',
            code: code ?? null,
         };
   }
}

interface UserProviderProps {
   children?: React.ReactNode
}

export const UserProvider = ({ children }: UserProviderProps) => {
   const [userId, setUserId] = React.useState<string>(DEV_AUTH ? 'dev-user-00000000' : '');
   const [displayName, setDisplayName] = React.useState<string>(DEV_AUTH ? 'Dev User' : DefaultUserName);
   const [email, setEmail] = React.useState<string>(DEV_AUTH ? 'dev-user@example.com' : '');
   // Flips true once initial sign-in state is known (MSAL settled + cookie session probed).
   const [authResolved, setAuthResolved] = React.useState<boolean>(DEV_AUTH);
   const [themeMode, setThemeModeRaw] = React.useState<ThemeMode>(
      (localStorage.getItem('themeMode') as ThemeMode) || ThemeModes.Light,
   );
   // The unique display Name and DefaultDomain are server-owned (provisioned on
   // first login); we hold the last-known value here and re-sync on login.
   const [userName, setUserNameRaw] = React.useState<string>('');
   const [defaultDomain, setDefaultDomainRaw] = React.useState<string>('');
   const [defaultLicense, setDefaultLicenseRaw] = React.useState<string>('');
   const [defaultLicenseUrl, setDefaultLicenseUrlRaw] = React.useState<string>('');
   const [defaultCopyrightHolder, setDefaultCopyrightHolderRaw] = React.useState<string>('');
   // Optimistic true so returning users don't see a flash; set to false if server
   // confirms the user hasn't accepted yet.
   const [termsAccepted, setTermsAccepted] = React.useState<boolean>(true);
   // Defaults closed: until the server says otherwise, the beta formats stay hidden.
   const [betaTester, setBetaTester] = React.useState<boolean>(false);
   // Defaults closed too: nobody is treated as an admin until the server says so.
   const [admin, setAdmin] = React.useState<boolean>(false);

   // Single setter that updates React state, mirrors to localStorage (so
   // pre-auth reloads still respect the last choice), and best-effort
   // pushes the new value to the UserPreferences row on the server.
   const setThemeMode = React.useCallback((value: ThemeMode) => {
      setThemeModeRaw(value);
      try { localStorage.setItem('themeMode', value); } catch { /* private mode */ }
      // Server persist is auth-gated; an unauthenticated user just keeps
      // the localStorage copy and we re-sync on next login.
      api.put('/opcua/v1/user/preferences', { themeMode: value })
         .catch(() => { /* best-effort */ });
   }, []);

   // Persist a new display name. The server validates uniqueness and returns
   // 409 with a message when taken — surface that to the account drawer by
   // rejecting with a normalized Error.
   const setUserName = React.useCallback(async (value: string) => {
      try {
         const res = await api.put<{ name?: string }>(
            '/opcua/v1/user/preferences', { name: value });
         setUserNameRaw(res.data?.name ?? value);
      } catch (err) {
         const data = (err as { response?: { data?: { error?: { message?: string }, message?: string } } })?.response?.data;
         throw new Error(data?.error?.message ?? data?.message ?? 'Could not save the name.');
      }
   }, []);

   // Persist a new default domain (best-effort; optimistic local update).
   const setDefaultDomain = React.useCallback(async (value: string) => {
      setDefaultDomainRaw(value);
      try {
         const res = await api.put<{ defaultDomain?: string }>(
            '/opcua/v1/user/preferences', { defaultDomain: value });
         if (res.data?.defaultDomain != null) setDefaultDomainRaw(res.data.defaultDomain);
      } catch { /* best-effort */ }
   }, []);

   // Persist a new default license + URL (best-effort; optimistic local update).
   const setDefaultLicense = React.useCallback(async (license: string, licenseUrl: string) => {
      setDefaultLicenseRaw(license);
      setDefaultLicenseUrlRaw(licenseUrl);
      try {
         const res = await api.put<{ defaultLicense?: string, defaultLicenseUrl?: string }>(
            '/opcua/v1/user/preferences', { defaultLicense: license, defaultLicenseUrl: licenseUrl });
         if (res.data?.defaultLicense != null) setDefaultLicenseRaw(res.data.defaultLicense);
         if (res.data?.defaultLicenseUrl != null) setDefaultLicenseUrlRaw(res.data.defaultLicenseUrl);
      } catch { /* best-effort */ }
   }, []);

   // Persist a new default copyright holder (best-effort; optimistic local update).
   const setDefaultCopyrightHolder = React.useCallback(async (value: string) => {
      setDefaultCopyrightHolderRaw(value);
      try {
         const res = await api.put<{ defaultCopyrightHolder?: string }>(
            '/opcua/v1/user/preferences', { defaultCopyrightHolder: value });
         if (res.data?.defaultCopyrightHolder != null) setDefaultCopyrightHolderRaw(res.data.defaultCopyrightHolder);
      } catch { /* best-effort */ }
   }, []);

   // Hydrate themeMode from the server once the user is authenticated. The
   // server is authoritative — if the user toggled dark mode on a different
   // device, this brings it across. localStorage stays as the offline
   // fallback for the moment before this fires.
   const hasFetchedPrefsForUser = React.useRef<string | null>(null);
   React.useEffect(() => {
      if (!userId) return;
      if (hasFetchedPrefsForUser.current === userId) return;
      hasFetchedPrefsForUser.current = userId;
      api.get<{ themeMode?: string, name?: string, defaultDomain?: string, defaultLicense?: string, defaultLicenseUrl?: string, defaultCopyrightHolder?: string, termsAccepted?: boolean, betaTester?: boolean, admin?: boolean }>('/opcua/v1/user/preferences')
         .then((res) => {
            const serverTheme = res.data?.themeMode;
            if (serverTheme === ThemeModes.Light || serverTheme === ThemeModes.Dark) {
               setThemeModeRaw(serverTheme);
               try { localStorage.setItem('themeMode', serverTheme); } catch { /* ignore */ }
            }
            if (res.data?.name != null) setUserNameRaw(res.data.name);
            if (res.data?.defaultDomain != null) setDefaultDomainRaw(res.data.defaultDomain);
            if (res.data?.defaultLicense != null) setDefaultLicenseRaw(res.data.defaultLicense);
            if (res.data?.defaultLicenseUrl != null) setDefaultLicenseUrlRaw(res.data.defaultLicenseUrl);
            if (res.data?.defaultCopyrightHolder != null) setDefaultCopyrightHolderRaw(res.data.defaultCopyrightHolder);
            setTermsAccepted(res.data?.termsAccepted ?? false);
            setBetaTester(res.data?.betaTester ?? false);
            setAdmin(res.data?.admin ?? false);
         })
         .catch(() => { /* best-effort */ });
   }, [userId]);
   const [loginError, setLoginError] = React.useState<string | null>(null);
   const [loginErrorCode, setLoginErrorCode] = React.useState<string | null>(null);
   const { instance, accounts, inProgress } = useMsal();
   const account = accounts[0] ?? null;

   // Listen for MSAL events so we can surface a friendly message when AAD
   // rejects the sign-in (e.g. account not invited to the tenant).
   React.useEffect(() => {
      if (DEV_AUTH) return;
      // Nothing drives MSAL when the deployment has no Azure AD, and the instance is a
      // placeholder — registering callbacks or processing a redirect on it is meaningless.
      if (!isAzureAdEnabled()) return;
      // MSAL v5 routes login-redirect failures through ACQUIRE_TOKEN_FAILURE
      // (login is internally a token acquisition); LOGIN_FAILURE was removed
      // from the enum. The synchronous reject from loginRedirect is handled
      // separately in login() below.
      const callbackId = instance.addEventCallback((event: EventMessage) => {
         if (event.eventType === EventType.ACQUIRE_TOKEN_FAILURE) {
            const { message, code } = describeAuthError(event.error);
            console.error('MSAL auth failure', code, event.error);
            setLoginError(message);
            setLoginErrorCode(code);
         } else if (event.eventType === EventType.LOGIN_SUCCESS) {
            setLoginError(null);
            setLoginErrorCode(null);
         }
      });

      // Belt-and-braces: explicitly observe handleRedirectPromise so we catch
      // AAD redirect rejections (e.g. AADSTS50020) that don't always surface
      // as ACQUIRE_TOKEN_FAILURE in MSAL v5. handleRedirectPromise is safe to
      // call multiple times — subsequent calls return the cached settled
      // promise, so this composes cleanly with MsalProvider's internal call.
      instance.handleRedirectPromise()
         .catch(async (error) => {
            const { message, code } = describeAuthError(error);
            console.error('handleRedirectPromise rejected', code, error);
            if (code === 'interaction_in_progress') {
               // Stale interaction state from a previous failed redirect — clear it
               // silently so the user can just click Login and try again.
               await instance.clearCache();
               return;
            }
            setLoginError(message);
            setLoginErrorCode(code);
         });

      return () => {
         if (callbackId) instance.removeEventCallback(callbackId);
      };
   }, [instance]);

   const clearLoginError = React.useCallback(() => {
      setLoginError(null);
      setLoginErrorCode(null);
   }, []);

   const acceptTerms = React.useCallback(async () => {
      await api.post('/opcua/v1/user/terms');
      setTermsAccepted(true);
   }, []);

   React.useEffect(() => {
      if (DEV_AUTH) return; // Skip auth sync in dev auth mode
      if (account) {
         instance.setActiveAccount(account);
         // Key the client identity on the lowercased email to mirror the server (which
         // now unifies both sign-in paths on email) and keep the preferences cache key stable.
         setUserId((account.username ?? '').toLowerCase());
         setDisplayName(account.name ?? account.username ?? '');
         setEmail(account.username ?? '');
         setAuthResolved(true);
         return;
      }
      // No MSAL account. Wait for any in-flight MSAL interaction to settle before deciding,
      // then probe for an existing email-code cookie session (the cookie is HttpOnly, so the
      // only way to detect it is to ask the server).
      if (inProgress !== InteractionStatus.None) return;
      let cancelled = false;
      (async () => {
         try {
            const res = await api.get<{ email?: string; displayName?: string }>('/auth/session');
            if (cancelled) return;
            const sessionEmail = res.data?.email ?? '';
            setUserId(sessionEmail.toLowerCase());
            setEmail(sessionEmail);
            setDisplayName(res.data?.displayName ?? sessionEmail);
         } catch {
            if (cancelled) return;
            setUserId('');
            setEmail('');
            setDisplayName('');
         } finally {
            if (!cancelled) setAuthResolved(true);
         }
      })();
      return () => { cancelled = true; };
   }, [account, inProgress, instance]);

   const login = () => {
      if (DEV_AUTH) return;
      if (!isAzureAdEnabled()) return;
      // Clear any prior failure so the LoginPage doesn't show stale text
      // while the redirect round-trip is in progress.
      clearLoginError();
      instance
         .loginRedirect(loginRequest)
         .catch(async (error) => {
            const { message, code } = describeAuthError(error);
            console.error('MSAL loginRedirect rejected', code, error);
            if (code === 'interaction_in_progress') {
               // Stale interaction state — clear cache and retry once automatically.
               await instance.clearCache();
               instance.loginRedirect(loginRequest).catch((retryError) => {
                  const { message: m, code: c } = describeAuthError(retryError);
                  console.error('MSAL loginRedirect retry rejected', c, retryError);
                  setLoginError(m);
                  setLoginErrorCode(c);
               });
               return;
            }
            setLoginError(message);
            setLoginErrorCode(code);
         });
   };

   // Email-code path — step 1: request a one-time code. Errors surface a displayable message.
   const requestEmailCode = React.useCallback(async (emailAddr: string) => {
      clearLoginError();
      try {
         await api.post('/auth/request-code', { email: emailAddr.trim() });
      } catch (err) {
         throw new Error(extractAuthMessage(err, 'Could not send the code. Please try again.'));
      }
   }, [clearLoginError]);

   // Email-code path — step 2: submit the code. On success the server sets the session
   // cookie; adopt the returned identity locally so the app shell renders immediately.
   const verifyEmailCode = React.useCallback(async (emailAddr: string, code: string) => {
      clearLoginError();
      try {
         const res = await api.post<{ email?: string }>('/auth/verify-code', {
            email: emailAddr.trim(),
            code: code.trim(),
         });
         const verifiedEmail = (res.data?.email ?? emailAddr.trim()).toLowerCase();
         setUserId(verifiedEmail);
         setEmail(verifiedEmail);
         setDisplayName(verifiedEmail);
         setAuthResolved(true);
      } catch (err) {
         throw new Error(extractAuthMessage(err, 'That code is invalid or has expired.'));
      }
   }, [clearLoginError]);

   // Test mode — sign in to the shared account. The server sets the same session cookie the
   // email-code path uses, so everything downstream is identical; only the way the cookie was
   // obtained differs. The endpoint 404s unless the deployment enabled test mode.
   const signInTestMode = React.useCallback(async () => {
      clearLoginError();
      try {
         const res = await api.post<{ email?: string; displayName?: string }>('/auth/test-mode');
         const testEmail = (res.data?.email ?? '').toLowerCase();
         setUserId(testEmail);
         setEmail(testEmail);
         setDisplayName(res.data?.displayName ?? 'Test Mode');
         setAuthResolved(true);
      } catch (err) {
         throw new Error(extractAuthMessage(err, 'Test mode is not available on this deployment.'));
      }
   }, [clearLoginError]);

   const logout = async () => {
      if (DEV_AUTH) return;
      // Reliable LOCAL sign-out for both paths. The failure we're guarding against: MSAL
      // caches the account in sessionStorage, so if it isn't truly removed the next page
      // load silently re-adopts the Microsoft account and the login choice never appears
      // ("always forces Microsoft"). We clear it three ways, strongest last.

      // 1. Clear MSAL's cache FIRST. This is deliberate ordering:
      //    (a) it's what was auto-logging the user back in on every load;
      //    (b) doing it before ANY network call means the /auth/logout request's axios
      //        interceptor sees no cached account, so it won't attempt an interactive token
      //        popup — that popup can hang and prevent the sign-out from completing.
      //    MSAL keeps its whole cache in sessionStorage (cacheLocation: 'sessionStorage',
      //    storeAuthStateInCookie: false), and clearCache() doesn't reliably drop the account
      //    entry, so we clear the store outright. Prefs live in localStorage, so this only
      //    removes auth state.
      try { window.sessionStorage.clear(); } catch { /* private mode */ }
      try { instance.setActiveAccount(null); } catch { /* ignore */ }

      // 2. Clear the email-code cookie server-side (fast now — no token acquisition).
      try { await api.post('/auth/logout'); } catch { /* best-effort */ }

      setUserId('');
      setEmail('');
      setDisplayName('');
      // 3. Full reload from / → no account, no cookie → RequireAuth shows the login choice.
      window.location.assign('/');
   };

   const userContext = {
      userId,
      setUserId,
      displayName,
      setDisplayName,
      email,
      userName,
      setUserName,
      defaultDomain,
      setDefaultDomain,
      defaultLicense,
      defaultLicenseUrl,
      setDefaultLicense,
      defaultCopyrightHolder,
      setDefaultCopyrightHolder,
      themeMode,
      setThemeMode,
      isAuthenticated: DEV_AUTH || !!userId,
      authResolved,
      login,
      logout,
      requestEmailCode,
      verifyEmailCode,
      signInTestMode,
      azureAdEnabled: isAzureAdEnabled(),
      testMode: getRuntimeConfig().testMode,
      cloudLibraryEnabled: getRuntimeConfig().cloudLibraryEnabled,
      loginError,
      loginErrorCode,
      clearLoginError,
      termsAccepted,
      acceptTerms,
      betaTester,
      admin,
   } as UserContextType;

   return (
      <UserContext.Provider value={userContext}>
         <ThemeProvider theme={(themeMode == ThemeModes.Dark) ? DarkTheme : LightTheme}>
            {children}
            {!!userId && !termsAccepted && (
               <TermsConsentDialog onAgree={acceptTerms} onSignOut={logout} />
            )}
            {/* Test mode is a property of the deployment, so this shows whether or not anyone is
                signed in: nobody should mistake an evaluation instance for a real one. */}
            {getRuntimeConfig().testMode && (
               <Chip
                  label="Test Mode"
                  color="warning"
                  size="small"
                  sx={{
                     position: 'fixed',
                     bottom: 8,
                     left: 8,
                     zIndex: (theme) => theme.zIndex.tooltip,
                     fontWeight: 'bold',
                  }}
               />
            )}
         </ThemeProvider>
      </UserContext.Provider>
   );
};

export default UserProvider;
