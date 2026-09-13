import * as React from 'react';

import Dialog from '@mui/material/Dialog';
import DialogContent from '@mui/material/DialogContent';

import LoginForm from './LoginForm';

interface LoginDialogProps {
   open: boolean;
   onClose: () => void;
}

/**
 * Modal sign-in dialog opened from the "Log In" buttons. Presents the shared LoginForm and
 * closes itself once the user is signed in via the email-code path. (The Microsoft option
 * redirects away, so it dismisses the whole page rather than just the modal.)
 */
export const LoginDialog: React.FC<LoginDialogProps> = ({ open, onClose }) => {
   return (
      <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth>
         <DialogContent sx={{ p: 4 }}>
            <LoginForm onSuccess={onClose} />
         </DialogContent>
      </Dialog>
   );
};

export default LoginDialog;
