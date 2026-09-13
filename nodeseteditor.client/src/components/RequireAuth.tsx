import * as React from 'react';
import Box from '@mui/material/Box';
import CircularProgress from '@mui/material/CircularProgress';

import { UserContext } from '../UserContext';
import LoginPage from '../pages/LoginPage';

const DEV_AUTH = import.meta.env.VITE_DEV_AUTH === 'true';

interface RequireAuthProps {
   children: React.ReactNode;
}

/**
 * Gates the app behind sign-in, supporting BOTH paths: Azure AD (MSAL) and the
 * email-code cookie session. Both resolve to context.isAuthenticated. While the
 * initial state is still being determined (MSAL settling, cookie session probe),
 * render a spinner rather than flashing the login page.
 */
export const RequireAuth: React.FC<RequireAuthProps> = ({ children }) => {
   const context = React.useContext(UserContext);

   if (DEV_AUTH) {
      return <>{children}</>;
   }

   if (!context.authResolved) {
      return (
         <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: '100%', minHeight: '100%', p: 4 }}>
            <CircularProgress />
         </Box>
      );
   }

   return context.isAuthenticated ? <>{children}</> : <LoginPage />;
};

export default RequireAuth;
