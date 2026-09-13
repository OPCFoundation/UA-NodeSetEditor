import * as React from 'react';

const DEFAULT_HELP_URL = 'https://reference.opcfoundation.org';
const WINDOW_NAME = 'opcua-nodeset-help';
const WINDOW_FEATURES = 'width=1200,height=800,resizable=yes,scrollbars=yes';

export const HELP_SEARCH_URL = (term: string) =>
   `https://reference.opcfoundation.org/search?q=${encodeURIComponent(term)}`;

/**
 * Restrict help-window navigation to http(s) URLs. A node's free-text
 * `documentation` field (imported/shared model content) flows into openHelp /
 * navigateHelp; without this guard a value like `javascript:fetch(...)` would
 * execute in the app's own origin when assigned to window.open /
 * location.href — stored XSS. Anything that isn't http/https falls back to the
 * reference home page.
 */
const safeHelpUrl = (url: string): string => {
   try {
      const u = new URL(url, window.location.origin);
      return u.protocol === 'http:' || u.protocol === 'https:' ? u.href : DEFAULT_HELP_URL;
   } catch {
      return DEFAULT_HELP_URL;
   }
};

export interface HelpContextType {
   /** True while the named help window is open. */
   isHelpOpen: boolean;
   /**
    * Open the help window (or focus + navigate it if already open).
    * Defaults to the OPC Foundation reference home page.
    */
   openHelp: (url?: string) => void;
   /**
    * Navigate the help window to `url` only if it is already open.
    * No-ops when the window is closed.
    */
   navigateHelp: (url: string) => void;
}

export const HelpContext = React.createContext<HelpContextType>({
   isHelpOpen: false,
   openHelp: () => undefined,
   navigateHelp: () => undefined,
});

export const HelpProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
   const windowRef = React.useRef<Window | null>(null);
   const pollRef = React.useRef<ReturnType<typeof setInterval> | null>(null);
   const [isHelpOpen, setIsHelpOpen] = React.useState(false);

   const stopPolling = React.useCallback(() => {
      if (pollRef.current !== null) {
         clearInterval(pollRef.current);
         pollRef.current = null;
      }
   }, []);

   const startPolling = React.useCallback(() => {
      stopPolling();
      pollRef.current = setInterval(() => {
         if (windowRef.current?.closed) {
            windowRef.current = null;
            setIsHelpOpen(false);
            stopPolling();
         }
      }, 800);
   }, [stopPolling]);

   // Clean up on unmount
   React.useEffect(() => () => stopPolling(), [stopPolling]);

   const openHelp = React.useCallback((url: string = DEFAULT_HELP_URL) => {
      const safeUrl = safeHelpUrl(url);
      if (!windowRef.current || windowRef.current.closed) {
         windowRef.current = window.open(safeUrl, WINDOW_NAME, WINDOW_FEATURES);
         if (windowRef.current) {
            setIsHelpOpen(true);
            startPolling();
         }
      } else {
         windowRef.current.location.href = safeUrl;
         windowRef.current.focus();
      }
   }, [startPolling]);

   const navigateHelp = React.useCallback((url: string) => {
      if (windowRef.current && !windowRef.current.closed) {
         windowRef.current.location.href = safeHelpUrl(url);
      }
   }, []);

   return (
      <HelpContext.Provider value={{ isHelpOpen, openHelp, navigateHelp }}>
         {children}
      </HelpContext.Provider>
   );
};
