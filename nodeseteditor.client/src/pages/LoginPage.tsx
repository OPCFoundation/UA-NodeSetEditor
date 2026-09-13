import * as React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';

import Box from '@mui/material/Box';
import Paper from '@mui/material/Paper';

import LoginForm from '../components/LoginForm';

/**
 * Full-page sign-in screen. Used by the RequireAuth gate (when an unauthenticated user hits a
 * protected page) and by the standalone /login route. The "Log In" buttons open the modal
 * LoginDialog instead — this full-page form is the gate/fallback, not the primary entry point.
 */
const LoginPage: React.FC = () => {
   const navigate = useNavigate();
   const location = useLocation();

   // On the standalone /login route nothing gates us, so send the now-authenticated user home.
   // Under RequireAuth (a protected path), that component swaps to the app on its own.
   const handleSuccess = React.useCallback(() => {
      if (location.pathname === '/login') {
         navigate('/', { replace: true });
      }
   }, [location.pathname, navigate]);

   return (
      <Box
         sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: '100%',
            minHeight: '100%',
            p: 4,
         }}
      >
         <Paper elevation={2} sx={{ p: 5, maxWidth: 480, width: '100%' }}>
            <LoginForm onSuccess={handleSuccess} />
         </Paper>
      </Box>
   );
};

export default LoginPage;
