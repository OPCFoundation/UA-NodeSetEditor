import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Link from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

import { safeOpcFoundationUrl } from '../utils/safeUrl';

const TERMS_URL = safeOpcFoundationUrl('https://opcfoundation.org/license/services/1.0/');

interface TermsConsentDialogProps {
   onAgree: () => void;
   onSignOut: () => void;
}

const TermsConsentDialog: React.FC<TermsConsentDialogProps> = ({ onAgree, onSignOut }) => {
   const { t } = useTranslation();
   return (
      <Dialog open maxWidth="sm" fullWidth disableEscapeKeyDown>
         <DialogTitle>
            <Typography variant="h6" component="span" sx={{ fontWeight: 'bold' }}>
               {t('terms.title')}
            </Typography>
         </DialogTitle>
         <DialogContent>
            <Stack spacing={3} sx={{ mt: 1 }}>
               <Typography variant="body1">
                  {t('terms.intro')}
               </Typography>
               {TERMS_URL && (
                  <Typography variant="body1">
                     <Link href={TERMS_URL} target="_blank" rel="noopener noreferrer">
                        {t('terms.linkText')}
                     </Link>
                  </Typography>
               )}
            </Stack>
         </DialogContent>
         <DialogActions>
            <Button onClick={onSignOut} color="inherit">
               {t('terms.signOut')}
            </Button>
            <Button onClick={onAgree} variant="contained" color="primary">
               {t('terms.agree')}
            </Button>
         </DialogActions>
      </Dialog>
   );
};

export default TermsConsentDialog;
