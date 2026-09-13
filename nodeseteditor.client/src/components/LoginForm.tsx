import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Divider from '@mui/material/Divider';
import IconButton from '@mui/material/IconButton';
import Link from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import CloseIcon from '@mui/icons-material/Close';
import HelpOutlineIcon from '@mui/icons-material/HelpOutline';
import MicrosoftIcon from '@mui/icons-material/Microsoft';
import EmailIcon from '@mui/icons-material/Email';
import ScienceIcon from '@mui/icons-material/Science';

import { UserContext } from '../UserContext';
import { safeOpcFoundationUrl } from '../utils/safeUrl';

const PRIVACY_POLICY_URL = safeOpcFoundationUrl('https://opcfoundation.org/privacy-policy/');

type Step = 'email' | 'code';

interface LoginFormProps {
   /**
    * Called after a successful email-code sign-in. Presentation-specific: the modal passes its
    * close handler; the standalone /login page navigates home; the RequireAuth gate passes
    * nothing (it swaps to the app on its own when isAuthenticated flips).
    */
   onSuccess?: () => void;
}

/**
 * Presentation-agnostic sign-in form: passwordless email code (primary) with Microsoft as a
 * secondary option. Wrapped by LoginPage (full-page gate) and LoginDialog (modal).
 */
export const LoginForm: React.FC<LoginFormProps> = ({ onSuccess }) => {
   const { t } = useTranslation();
   const context = React.useContext(UserContext);
   const [helpOpen, setHelpOpen] = React.useState(false);

   const [step, setStep] = React.useState<Step>('email');
   const [emailInput, setEmailInput] = React.useState('');
   const [codeInput, setCodeInput] = React.useState('');
   const [busy, setBusy] = React.useState(false);
   const [error, setError] = React.useState<string | null>(null);
   const [notice, setNotice] = React.useState<string | null>(null);

   const emailValid = /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(emailInput.trim());

   const sendCode = async () => {
      if (!emailValid || busy) return;
      setBusy(true);
      setError(null);
      try {
         await context.requestEmailCode(emailInput.trim());
         setStep('code');
         setNotice(t('login.codeSent', 'We sent a sign-in code to {{email}}. It expires in 10 minutes.', { email: emailInput.trim() }));
      } catch (e) {
         setError((e as Error).message);
      } finally {
         setBusy(false);
      }
   };

   const verifyCode = async () => {
      if (!codeInput.trim() || busy) return;
      setBusy(true);
      setError(null);
      try {
         await context.verifyEmailCode(emailInput.trim(), codeInput.trim());
         onSuccess?.();
      } catch (e) {
         setError((e as Error).message);
      } finally {
         setBusy(false);
      }
   };

   const resetToEmail = () => {
      setStep('email');
      setCodeInput('');
      setError(null);
      setNotice(null);
   };

   return (
      <Stack spacing={3} alignItems="center">
         <Typography variant="h5" sx={{ fontWeight: 'bolder', textAlign: 'center' }}>
            {t('login.title')}
         </Typography>
         <Typography
            variant="body1"
            sx={{ fontSize: 'smaller', fontWeight: 'lighter', textAlign: 'center' }}
         >
            {t('login.summary')}
         </Typography>

         {(error || context.loginError) && (
            <Alert
               severity="error"
               sx={{ width: '100%' }}
               action={
                  <IconButton
                     aria-label={t('common.close', 'Close')}
                     color="inherit"
                     size="small"
                     onClick={() => { setError(null); context.clearLoginError(); }}
                  >
                     <CloseIcon fontSize="inherit" />
                  </IconButton>
               }
            >
               {context.loginErrorCode && (
                  <AlertTitle sx={{ fontWeight: 'bold' }}>{context.loginErrorCode}</AlertTitle>
               )}
               {error ?? context.loginError}
            </Alert>
         )}

         {notice && step === 'code' && (
            <Alert severity="info" sx={{ width: '100%' }}>{notice}</Alert>
         )}

         {/* Primary path: passwordless email code. */}
         {step === 'email' ? (
            <Stack
               component="form"
               spacing={2}
               sx={{ width: '100%' }}
               onSubmit={(e) => { e.preventDefault(); void sendCode(); }}
            >
               <TextField
                  fullWidth
                  type="email"
                  autoFocus
                  label={t('login.emailLabel', 'Email address')}
                  placeholder={t('login.emailPlaceholder', 'you@example.com')}
                  value={emailInput}
                  onChange={(e) => setEmailInput(e.target.value)}
                  disabled={busy}
               />
               <Button
                  type="submit"
                  fullWidth
                  variant="contained"
                  color="primary"
                  startIcon={<EmailIcon />}
                  disabled={!emailValid || busy}
               >
                  {t('login.sendCode', 'Email me a sign-in code')}
               </Button>
            </Stack>
         ) : (
            <Stack
               component="form"
               spacing={2}
               sx={{ width: '100%' }}
               onSubmit={(e) => { e.preventDefault(); void verifyCode(); }}
            >
               <TextField
                  fullWidth
                  autoFocus
                  label={t('login.codeLabel', 'Sign-in code')}
                  placeholder="123456"
                  value={codeInput}
                  onChange={(e) => setCodeInput(e.target.value.replace(/\D/g, '').slice(0, 6))}
                  slotProps={{ htmlInput: { inputMode: 'numeric', maxLength: 6, style: { letterSpacing: '0.4em' } } }}
                  disabled={busy}
               />
               <Button
                  type="submit"
                  fullWidth
                  variant="contained"
                  color="primary"
                  disabled={!codeInput.trim() || busy}
               >
                  {t('login.verify', 'Sign in')}
               </Button>
               <Stack direction="row" justifyContent="space-between">
                  <Link component="button" type="button" variant="body2" onClick={() => { if (!busy) void sendCode(); }}>
                     {t('login.resend', 'Resend code')}
                  </Link>
                  <Link component="button" type="button" variant="body2" onClick={resetToEmail}>
                     {t('login.changeEmail', 'Use a different email')}
                  </Link>
               </Stack>
            </Stack>
         )}

         {/* Secondary paths. Both are deployment-dependent (GET /api/config): a self-hosted
             instance has no Azure AD tenant, and test mode is off wherever real data lives. */}
         {(context.azureAdEnabled || context.testMode) && (
            <Divider flexItem sx={{ color: 'text.secondary', fontSize: 'smaller' }}>
               {t('login.or', 'or')}
            </Divider>
         )}

         {context.azureAdEnabled && (
            <Button
               fullWidth
               variant="outlined"
               color="primary"
               startIcon={<MicrosoftIcon />}
               onClick={() => context.login()}
            >
               {t('login.microsoft', 'Sign in with Microsoft')}
            </Button>
         )}

         {context.testMode && (
            <Stack spacing={1} sx={{ width: '100%' }}>
               <Button
                  fullWidth
                  variant="outlined"
                  color="warning"
                  startIcon={<ScienceIcon />}
                  disabled={busy}
                  onClick={() => {
                     setBusy(true);
                     setError(null);
                     context.signInTestMode()
                        .then(() => onSuccess?.())
                        .catch((e) => setError((e as Error).message))
                        .finally(() => setBusy(false));
                  }}
               >
                  {t('login.testMode', 'Continue in Test Mode')}
               </Button>
               <Typography variant="caption" sx={{ textAlign: 'center', color: 'text.secondary' }}>
                  {t('login.testModeWarning',
                     'Everyone signing in this way shares one account and one set of models.')}
               </Typography>
            </Stack>
         )}

         <Link component="button" type="button" variant="body2" onClick={() => setHelpOpen(true)} sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5 }}>
            <HelpOutlineIcon fontSize="inherit" /> {t('login.help')}
         </Link>

         <Dialog open={helpOpen} onClose={() => setHelpOpen(false)} maxWidth="sm" fullWidth>
            <DialogTitle>
               <Typography variant="h6" component="span" sx={{ fontWeight: 'bold' }}>
                  {t('login.helpTitle')}
               </Typography>
            </DialogTitle>
            <DialogContent>
               <Stack spacing={3} sx={{ mt: 1 }}>
                  <Typography variant="body1">
                     {t('login.helpAccess')}
                  </Typography>
                  <Typography variant="body1">
                     {t('login.helpConsent')}
                  </Typography>
                  {PRIVACY_POLICY_URL && (
                     <Typography variant="body1">
                        {t('login.helpPrivacyPrefix')}{' '}
                        <Link href={PRIVACY_POLICY_URL} target="_blank" rel="noopener noreferrer">
                           {PRIVACY_POLICY_URL}
                        </Link>
                     </Typography>
                  )}
               </Stack>
            </DialogContent>
            <DialogActions>
               <Button onClick={() => setHelpOpen(false)}>{t('common.close')}</Button>
            </DialogActions>
         </Dialog>
      </Stack>
   );
};

export default LoginForm;
