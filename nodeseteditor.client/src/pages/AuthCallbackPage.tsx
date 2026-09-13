import * as React from 'react';
import { useNavigate } from 'react-router-dom';
import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';

import Box from '@mui/material/Box';
import CircularProgress from '@mui/material/CircularProgress';
import Typography from '@mui/material/Typography';

/**
 * Landing page for the MSAL redirect (VITE_REDIRECT_URL = /login/success).
 * MsalProvider handles the auth-code exchange internally via
 * handleRedirectPromise; this component just waits for the interaction to
 * settle and then leaves the route. Without a registered route React Router
 * renders nothing here, the user is stuck on a blank page with a "No routes
 * matched" warning, and the auth flow appears to fail.
 */
const AuthCallbackPage: React.FC = () => {
   const isAuthenticated = useIsAuthenticated();
   const { inProgress } = useMsal();
   const navigate = useNavigate();

   React.useEffect(() => {
      // Wait until MSAL has finished its interaction (handleRedirectPromise
      // resolved, login/token acquisition complete). Bouncing earlier races
      // with MSAL's own navigation and can clear the hash before it's parsed.
      if (inProgress !== InteractionStatus.None) return;
      navigate('/', { replace: true });
   }, [inProgress, isAuthenticated, navigate]);

   return (
      <Box
         sx={{
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            justifyContent: 'center',
            height: '100vh',
            gap: 2,
         }}
      >
         <CircularProgress />
         <Typography variant="body2" color="text.secondary">
            Finishing sign-in…
         </Typography>
      </Box>
   );
};

export default AuthCallbackPage;
