import * as React from 'react';
import { type ThemeMode, ThemeModes } from './theme';

export type UserContextType = {
   userId: string,
   setUserId: (value: string) => void,
   displayName: string,
   setDisplayName: (value: string) => void,
   /** Email/UPN of the signed-in user, or empty string. From MSAL account.username. */
   email: string,
   /**
    * The user's globally-unique display name, shown to other users (workspace
    * owner, shared-model creator). Provisioned server-side on first login from
    * the email; editable from the account drawer.
    */
   userName: string,
   /**
    * Persists a new display name. Resolves on success; rejects with an Error
    * whose message describes the failure (e.g. the name is already taken).
    */
   setUserName: (value: string) => Promise<void>,
   /** Default domain used to seed newly created namespace URIs. */
   defaultDomain: string,
   /** Persists a new default domain (best-effort). */
   setDefaultDomain: (value: string) => Promise<void>,
   /** Default license identifier prefilled into the create-model dialog. */
   defaultLicense: string,
   /** Reference URL for the default license when it is a custom ("Other") license. */
   defaultLicenseUrl: string,
   /** Persists a new default license (and its URL for a custom license). */
   setDefaultLicense: (license: string, licenseUrl: string) => Promise<void>,
   /** Default copyright holder prefilled into the create-model dialog. */
   defaultCopyrightHolder: string,
   /** Persists a new default copyright holder (best-effort). */
   setDefaultCopyrightHolder: (value: string) => Promise<void>,
   themeMode?: ThemeMode,
   setThemeMode: (value: ThemeMode) => void,
   /** True when a user is signed in via either path (Azure AD or email code). */
   isAuthenticated: boolean,
   /**
    * True once the initial sign-in state has been determined (MSAL settled and the
    * email-code cookie session probed). Gates the app shell so it doesn't flash the
    * login page before an existing session is discovered.
    */
   authResolved: boolean,
   /** Starts the "Sign in with Microsoft" (Azure AD) redirect flow. */
   login: () => void,
   logout: () => void,
   /**
    * Email-code path — step 1: requests a one-time code be emailed to the address.
    * Resolves on success; rejects with an Error whose message is safe to display.
    */
   requestEmailCode: (email: string) => Promise<void>,
   /**
    * Email-code path — step 2: submits the code. On success the server sets the
    * session cookie and this updates the signed-in identity. Rejects with a
    * displayable Error on an invalid/expired code or lockout.
    */
   verifyEmailCode: (email: string, code: string) => Promise<void>,
   /**
    * Signs in to the shared "Test Mode" account. Only meaningful when {@link testMode} is
    * true; the endpoint 404s otherwise.
    */
   signInTestMode: () => Promise<void>,
   /** True when the deployment offers "Sign in with Microsoft" (see runtimeConfig). */
   azureAdEnabled: boolean,
   /**
    * True when this deployment runs in test mode: one shared account, every feature on, no
    * mail provider needed. Everyone signed in this way is the same user.
    */
   testMode: boolean,
   /** True when a Cloud Library is configured; the feature is hidden otherwise. */
   cloudLibraryEnabled: boolean,
   /** Friendly message describing the most recent sign-in failure, or null. */
   loginError: string | null,
   /** AAD error code (e.g. "AADSTS50020") for logging/support, or null. */
   loginErrorCode: string | null,
   clearLoginError: () => void,
   /** True once the user has accepted the Terms of Use (server-confirmed). */
   termsAccepted: boolean,
   /** Records Terms of Use acceptance with the server. */
   acceptTerms: () => Promise<void>,
   /**
    * True when the server's BetaTesterDomains allow-list admits this user. Gates the
    * non-XML download formats; the server enforces the same rule, so this only decides
    * whether the options are offered.
    */
   betaTester: boolean,
}

export const DefaultUserName = 'Anonymous';

export const UserContext = React.createContext<UserContextType>({
   userId: '',
   setUserId: () => { },
   displayName: DefaultUserName,
   setDisplayName: () => { },
   email: '',
   userName: '',
   setUserName: () => Promise.resolve(),
   defaultDomain: '',
   setDefaultDomain: () => Promise.resolve(),
   defaultLicense: '',
   defaultLicenseUrl: '',
   setDefaultLicense: () => Promise.resolve(),
   defaultCopyrightHolder: '',
   setDefaultCopyrightHolder: () => Promise.resolve(),
   themeMode: ThemeModes.Light,
   setThemeMode: () => { },
   isAuthenticated: false,
   authResolved: false,
   login: () => { },
   logout: () => { },
   requestEmailCode: () => Promise.resolve(),
   verifyEmailCode: () => Promise.resolve(),
   signInTestMode: () => Promise.resolve(),
   azureAdEnabled: false,
   testMode: false,
   cloudLibraryEnabled: false,
   loginError: null,
   loginErrorCode: null,
   clearLoginError: () => { },
   termsAccepted: true,
   acceptTerms: () => Promise.resolve(),
   betaTester: false,
});
