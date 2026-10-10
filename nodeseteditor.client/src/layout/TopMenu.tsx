import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';

import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Toolbar from '@mui/material/Toolbar';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import { alpha } from '@mui/material/styles';
import { CategoryIcon as CategoryOutlinedIcon, FolderIcon as FolderOutlinedIcon, HelpOutlineIcon, HomeIcon as HomeOutlinedIcon, MenuIcon } from '../icons';

import SettingsMenu from './SettingsMenu';

interface TopMenuProps {
   title?: string | null;
   /** When provided, a hamburger is rendered on xs/sm screens that
       opens the mobile sidebar drawer. Omitted on pages without a
       sidebar (so the hamburger doesn't show on Welcome). */
   onOpenSidebar?: () => void;
   onOpenHelp?: () => void;
}

const sections = [
   { path: '/', labelKey: 'topMenu.home', fallback: 'Home', icon: <HomeOutlinedIcon /> },
   { path: '/model_library', labelKey: 'topMenu.workspaces', fallback: 'Workspaces', icon: <FolderOutlinedIcon /> },
   { path: '/type_library', labelKey: 'topMenu.models', fallback: 'Models', icon: <CategoryOutlinedIcon /> },
];

/**
 * Height of the nav pill, and of the logo beside it.
 *
 * Pinned to one constant rather than left to emerge from padding + the theme's button
 * minHeight (38px + 2×4px), so the two stay the same height by construction: changing the
 * button metrics can no longer silently desynchronise them.
 */
const NAV_PILL_HEIGHT = 46;

export const TopMenu = ({ title, onOpenSidebar, onOpenHelp }: TopMenuProps) => {
   const { t } = useTranslation();
   const navigate = useNavigate();
   const { pathname } = useLocation();

   const isActive = (path: string) => path === '/' ? pathname === '/' : pathname.startsWith(path);

   return (
      <Toolbar disableGutters sx={{ minHeight: { xs: 56, md: 64 }, px: { xs: 8, md: 20 }, gap: 12 }}>
         {onOpenSidebar && (
            <IconButton
               onClick={onOpenSidebar}
               color="inherit"
               sx={{ display: { xs: 'inline-flex', md: 'none' } }}
               aria-label={t('topMenu.openSidebar', 'Open sidebar')}
            >
               <MenuIcon />
            </IconButton>
         )}

         {/* Brand */}
         <Box
            onClick={() => navigate('/')}
            title={t('topMenu.homeTooltip', 'Home')}
            sx={{ display: 'flex', alignItems: 'center', gap: 12, cursor: 'pointer', minWidth: 0, flexShrink: 0 }}
         >
            <Box
               component="img"
               src="/opclogo-white.png"
               alt="OPC Foundation"
               sx={{ height: NAV_PILL_HEIGHT, display: { xs: 'none', sm: 'block' } }}
            />
            <Typography variant="h6" component="div" noWrap sx={{ fontSize: { xs: '1.05rem', md: '1.2rem' } }}>
               {t(title ?? '')}
            </Typography>
         </Box>

         {/* Primary navigation — segmented control, centred. Everything here is tinted
             from the bar's own foreground colour, so it works on the brand-blue bar in
             light mode and the near-black one in dark without branching on the mode. */}
         <Box sx={{ flexGrow: 1, display: 'flex', justifyContent: 'center', minWidth: 0 }}>
            <Box
               component="nav"
               sx={{
                  display: pathname === '/' ? 'none' : { xs: 'none', md: 'flex' },
                  gap: 4,
                  p: 4,
                  height: NAV_PILL_HEIGHT,
                  boxSizing: 'border-box',
                  borderRadius: 980,
                  bgcolor: (theme) => alpha(theme.palette.appBar.contrastText, 0.12),
               }}
            >
               {sections.map((s) => {
                  const active = isActive(s.path);
                  return (
                     <Button
                        key={s.path}
                        startIcon={s.icon}
                        onClick={() => navigate(s.path)}
                        aria-current={active ? 'page' : undefined}
                        sx={{
                           px: 18,
                           py: 6,
                           fontSize: '0.95rem',
                           // The active item is a solid pill in the bar's foreground colour
                           // with the bar's own colour as text — 6.7:1 on the blue bar and
                           // 15:1 in dark mode. A translucent fill with white text measured
                           // 4.1:1, under the 4.5:1 this text size needs.
                           color: (theme) => active
                              ? theme.palette.appBar.main
                              : alpha(theme.palette.appBar.contrastText, 0.85),
                           bgcolor: (theme) => active
                              ? theme.palette.appBar.contrastText
                              : 'transparent',
                           boxShadow: 'none',
                           '&:hover': {
                              bgcolor: (theme) => active
                                 ? theme.palette.appBar.contrastText
                                 : alpha(theme.palette.appBar.contrastText, 0.18),
                           },
                        }}
                     >
                        {t(s.labelKey, s.fallback)}
                     </Button>
                  );
               })}
            </Box>
         </Box>

         <Box sx={{ display: 'flex', alignItems: 'center', gap: 4, flexShrink: 0 }}>
            <Tooltip title={t('topMenu.helpTooltip', 'Help')}>
               <IconButton color="inherit" onClick={onOpenHelp} aria-label={t('topMenu.helpTooltip', 'Help')}>
                  <HelpOutlineIcon />
               </IconButton>
            </Tooltip>
            <SettingsMenu />
         </Box>
      </Toolbar>
   );
}
