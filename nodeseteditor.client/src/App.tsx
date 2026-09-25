import * as React from 'react';
import { Routes, Route } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

import Layout from './layout/Layout';
import { pages, type PageLayout } from "./AppRoutes";
import AuthCallbackPage from './pages/AuthCallbackPage';
import LoginPage from './pages/LoginPage';
import ValidationPage from './pages/ValidationPage';
import ConformanceUnitsPage from './pages/ConformanceUnitsPage';
import { RequireAuth } from './components/RequireAuth';

const App: React.FC = () => {
   const [title] = React.useState<string>('main.title');
   const { t } = useTranslation();

   React.useEffect(() => {
      document.title = t(title);
   }, [t, title]);

   return (
      <Routes>
         {pages.map((page: PageLayout) => {
            return <Route key={page.path} path={page.path} element={<Layout page={page}></Layout>} />;
         })}
         {/* Specification-validation page — reached from the Validate action on a
             private model. Kept out of the `pages` nav array (it needs a ?model=…
             context), so it's wired as an explicit route inside the same Layout. */}
         <Route
            path="/validation"
            element={<Layout page={{
               path: '/validation',
               title: 'validation.shortName',
               main: <RequireAuth><ValidationPage /></RequireAuth>,
               icon: null,
            }} />}
         />
         {/* Conformance-unit view — reached from a model's Conformance Units action.
             Kept out of the `pages` nav array for the same reason as /validation:
             it needs a model (?ns=…) to have anything to show. */}
         <Route
            path="/conformance_units"
            element={<Layout page={{
               path: '/conformance_units',
               title: 'conformanceUnits.shortName',
               main: <RequireAuth><ConformanceUnitsPage /></RequireAuth>,
               icon: null,
            }} />}
         />
         {/* Standalone login screen (email-code primary, Microsoft secondary).
             The "Log In" buttons navigate here so unauthenticated users always
             get the choice, even on public pages (e.g. the home wizard) that
             aren't wrapped in RequireAuth. */}
         <Route path="/login" element={<LoginPage />} />
         {/* MSAL redirect URI (VITE_REDIRECT_URL). Must be a real route or
             React Router renders nothing and the auth flow appears to hang. */}
         <Route path="/login/success" element={<AuthCallbackPage />} />
      </Routes>
   );
}

export default App;