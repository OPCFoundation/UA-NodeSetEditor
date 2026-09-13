import * as React from 'react';
import { useTranslation } from 'react-i18next';

import { useTheme } from '@mui/material/styles';
import PersonIcon from '@mui/icons-material/Person';
import LoginIcon from '@mui/icons-material/Login';
import LogoutIcon from '@mui/icons-material/Logout';
import DarkModeIcon from '@mui/icons-material/DarkMode';
import Avatar from '@mui/material/Avatar';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Drawer from '@mui/material/Drawer';
import List from '@mui/material/List';
import ListItemText from '@mui/material/ListItemText';
import ListItem from '@mui/material/ListItem';
import ListItemIcon from '@mui/material/ListItemIcon';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import Switch from '@mui/material/Switch';
import TextField from '@mui/material/TextField';

import { UserContext } from '../UserContext';
import { ThemeModes } from '../theme';
import { useLicenseOptions } from '../hooks/useLicenseOptions';
import { LicenseFields, type LicenseValue } from '../components/LicenseFields';
import LoginDialog from '../components/LoginDialog';

// Auth-state wrappers driven by the unified UserContext (covers both the Azure AD
// and email-code sign-in paths, and dev-auth mode).
const WhenAuthenticated: React.FC<{ children: React.ReactNode }> = ({ children }) => {
   const ctx = React.useContext(UserContext);
   return ctx.isAuthenticated ? <>{children}</> : null;
};

const WhenUnauthenticated: React.FC<{ children: React.ReactNode }> = ({ children }) => {
   const ctx = React.useContext(UserContext);
   return ctx.isAuthenticated ? null : <>{children}</>;
};

export default function SettingsMenu() {
   const [open, setOpen] = React.useState(false);
   const { t } = useTranslation();
   const context = React.useContext(UserContext);
   const [loginOpen, setLoginOpen] = React.useState(false);
   const darkMode = context.themeMode === ThemeModes.Dark;
   const theme = useTheme();
   const label = { inputProps: { 'aria-label': 'switch-list-label-darkmode' } };

   const onThemeChange = () => {
      context.setThemeMode((darkMode) ? ThemeModes.Light : ThemeModes.Dark);
   };

   // Editable account fields. Drafts mirror the context values (which arrive
   // asynchronously after login) and are committed on blur.
   const [nameDraft, setNameDraft] = React.useState('');
   const [domainDraft, setDomainDraft] = React.useState('');
   const [nameError, setNameError] = React.useState<string | null>(null);
   const [nameSaving, setNameSaving] = React.useState(false);
   const [copyrightDraft, setCopyrightDraft] = React.useState('');
   const [licenseDraft, setLicenseDraft] = React.useState<LicenseValue>({ license: '', licenseUrl: '' });

   const { data: licenseOptions } = useLicenseOptions();

   React.useEffect(() => { setNameDraft(context.userName ?? ''); setNameError(null); }, [context.userName]);
   React.useEffect(() => { setDomainDraft(context.defaultDomain ?? ''); }, [context.defaultDomain]);
   React.useEffect(() => { setCopyrightDraft(context.defaultCopyrightHolder ?? ''); }, [context.defaultCopyrightHolder]);
   React.useEffect(() => {
      setLicenseDraft({ license: context.defaultLicense ?? '', licenseUrl: context.defaultLicenseUrl ?? '' });
   }, [context.defaultLicense, context.defaultLicenseUrl]);

   const commitName = async () => {
      const value = nameDraft.trim();
      if (!value || value === context.userName) { setNameError(null); return; }
      setNameSaving(true);
      setNameError(null);
      try {
         await context.setUserName(value);
      } catch (e) {
         setNameError((e as Error).message);
      } finally {
         setNameSaving(false);
      }
   };

   const commitDomain = () => {
      const value = domainDraft.trim();
      if (value === context.defaultDomain) return;
      void context.setDefaultDomain(value);
   };

   const commitCopyright = () => {
      const value = copyrightDraft.trim();
      if (value === (context.defaultCopyrightHolder ?? '')) return;
      void context.setDefaultCopyrightHolder(value);
   };

   const commitLicense = () => {
      if (licenseDraft.license === (context.defaultLicense ?? '') &&
         licenseDraft.licenseUrl === (context.defaultLicenseUrl ?? '')) return;
      void context.setDefaultLicense(licenseDraft.license.trim(), licenseDraft.licenseUrl.trim());
   };

   const toggleKeyHandler = (open: boolean) => (e: React.KeyboardEvent<HTMLDivElement>) => {
      if (e.type === 'keydown' && (e.key === 'Tab' || e.key === 'Shift')) {
         return;
      }
      setOpen(open);
   };

   const toggleMouseHandler = (open: boolean) => () => {
      setOpen(open);
   };

   const list = () => (
      <Box
         sx={{ width: 350 }}
         role="presentation"
         onKeyDown={toggleKeyHandler(false)}
      >
         <Toolbar variant="dense" sx={{ backgroundColor: theme.palette.grey[200] }}>
            <Box sx={{ flexGrow: 1 }} />
            <WhenUnauthenticated>
               <Tooltip title={t("main.loginHelp")}>
                  <IconButton edge="start" color="inherit" size="small" sx={{ mr: 1 }} onClick={() => setLoginOpen(true)}>
                     <LoginIcon />
                  </IconButton>
               </Tooltip>
            </WhenUnauthenticated>
            <WhenAuthenticated>
               <Tooltip title={t("main.logoutHelp")}>
                  <IconButton edge="start" color="inherit" size="small" sx={{ mr: 1 }} onClick={() => context.logout()}>
                     <LogoutIcon />
                  </IconButton>
               </Tooltip>
            </WhenAuthenticated>
         </Toolbar>
         <List>
            <ListItem>
               <ListItemIcon>
                  <WhenAuthenticated>
                     <PersonIcon />
                     <Typography variant="body1" component="div" sx={{ px: 6 }} noWrap>
                        {context?.displayName}
                     </Typography>
                  </WhenAuthenticated>
                  <WhenUnauthenticated>
                     <Typography variant="body1" component="div" sx={{ px: 6 }} noWrap>
                        {t("main.notLoggedIn")}
                     </Typography>
                  </WhenUnauthenticated>
               </ListItemIcon>
            </ListItem>
            <WhenAuthenticated>
               <ListItem>
                  <TextField
                     fullWidth
                     size="small"
                     label={t('account.displayName', 'Display name')}
                     helperText={nameError ?? t('account.displayNameHelp', 'Shown to other users.')}
                     error={!!nameError}
                     disabled={nameSaving}
                     value={nameDraft}
                     onChange={(e) => setNameDraft(e.target.value)}
                     onKeyDown={(e) => e.stopPropagation()}
                     onBlur={() => void commitName()}
                  />
               </ListItem>
               <ListItem>
                  <TextField
                     fullWidth
                     size="small"
                     label={t('account.defaultDomain', 'Default domain')}
                     helperText={t('account.defaultDomainHelp', 'Used when creating new namespaces.')}
                     value={domainDraft}
                     onChange={(e) => setDomainDraft(e.target.value)}
                     onKeyDown={(e) => e.stopPropagation()}
                     onBlur={commitDomain}
                  />
               </ListItem>
               <ListItem>
                  <TextField
                     fullWidth
                     size="small"
                     label={t('account.defaultCopyrightHolder', 'Default copyright holder')}
                     helperText={t('account.defaultCopyrightHolderHelp', 'Prefilled when you create a new model.')}
                     value={copyrightDraft}
                     onChange={(e) => setCopyrightDraft(e.target.value)}
                     onKeyDown={(e) => e.stopPropagation()}
                     onBlur={commitCopyright}
                  />
               </ListItem>
               <ListItem onKeyDown={(e) => e.stopPropagation()} onBlur={commitLicense}>
                  <Box sx={{ width: '100%' }}>
                     <Typography variant="caption" color="text.secondary" sx={{ pl: '2px' }}>
                        {t('account.defaultLicense', 'Default license')}
                     </Typography>
                     <LicenseFields
                        options={licenseOptions ?? []}
                        license={licenseDraft.license}
                        licenseUrl={licenseDraft.licenseUrl}
                        onChange={setLicenseDraft}
                     />
                  </Box>
               </ListItem>
            </WhenAuthenticated>
            <ListItem>
               <ListItemIcon>
                  <DarkModeIcon />
               </ListItemIcon>
               <ListItemText id="switch-list-label-darkmode" primary={t('main.darkMode')} />
               <Switch
                  edge="end"
                  onChange={onThemeChange}
                  checked={darkMode}
                  {...label}
               />
            </ListItem>
      </List>
      </Box>
   );

   return (
      <div>
         <WhenAuthenticated>
            <Button
               onClick={toggleMouseHandler(true)}
               sx={{
                  // Theme-aware primary instead of hardcoded lightBlue,
                  // so dark mode's amber primary takes over the hover colour.
                  my: 2, display: 'flex', borderRightWidth: '0px', minWidth: '0px',
                  '&:hover .MuiAvatar-root': { bgcolor: 'primary.dark' },
                  '& .MuiSvgIcon-root': { color: 'primary.dark' },
                  '&:hover .MuiSvgIcon-root': { color: 'primary.light' },
                  '& div': { transition: 'color 0.2s' },
                  '&:hover div': { color: 'primary.dark' }
               }}
            >
               <Avatar sx={{ height: '24px', width: '24px' }}>
                  <PersonIcon sx={{ fontSize: '16px' }} />
               </Avatar>
               <Typography variant="body2" component="div" sx={{ px: 6 }} noWrap>
                  {context?.displayName}
               </Typography>
            </Button>
         </WhenAuthenticated>
         <WhenUnauthenticated>
            <Button
               onClick={() => setLoginOpen(true)}
               sx={{
                  my: 2, display: 'flex', borderRightWidth: '0px', minWidth: '0px',
                  '& .MuiAvatar-root': { bgcolor: 'primary.light', transition: 'background-color 0.2s' },
                  '&:hover .MuiAvatar-root': { bgcolor: 'primary.dark' },
                  '& .MuiSvgIcon-root': { color: 'primary.dark' },
                  '&:hover .MuiSvgIcon-root': { color: 'primary.light' },
                  '& div': { transition: 'color 0.2s' },
                  '&:hover div': { color: 'primary.dark' },
               }}
            >
               <Avatar sx={{ height: '24px', width: '24px' }}>
                  <LoginIcon sx={{ fontSize: '16px' }} />
               </Avatar>
               <Typography variant="body2" component="div" sx={{ px: 6 }} noWrap>
                  {t("main.login")}
               </Typography>
            </Button>
         </WhenUnauthenticated>
         <Drawer
            anchor={'right'}
            open={open}
            onClose={toggleMouseHandler(false)}
         >
            <Toolbar />
            {list()}
         </Drawer>
         <LoginDialog open={loginOpen} onClose={() => setLoginOpen(false)} />
      </div>
   );
}
