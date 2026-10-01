import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';

import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Toolbar from '@mui/material/Toolbar';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import CategoryOutlinedIcon from '@mui/icons-material/CategoryOutlined';
import FolderOutlinedIcon from '@mui/icons-material/FolderOutlined';
import HelpOutlineIcon from '@mui/icons-material/HelpOutline';
import HomeOutlinedIcon from '@mui/icons-material/HomeOutlined';
import MenuIcon from '@mui/icons-material/Menu';

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
   { path: '/model_library', labelKey: 'topMenu.projects', fallback: 'Servers', icon: <FolderOutlinedIcon /> },
   { path: '/type_library', labelKey: 'topMenu.models', fallback: 'Models', icon: <CategoryOutlinedIcon /> },
];

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
               src="/opclogo.png"
               alt="OPC Foundation"
               sx={{ height: 32, display: { xs: 'none', sm: 'block' }, borderRadius: 4 }}
            />
            <Typography variant="h6" component="div" noWrap sx={{ fontSize: { xs: '1.05rem', md: '1.2rem' } }}>
               {t(title ?? '')}
            </Typography>
         </Box>

         {/* Primary navigation — segmented control, centred */}
         <Box sx={{ flexGrow: 1, display: 'flex', justifyContent: 'center', minWidth: 0 }}>
            <Box
               component="nav"
               sx={{
                  display: pathname === '/' ? 'none' : { xs: 'none', md: 'flex' },
                  gap: 4,
                  p: 4,
                  borderRadius: 980,
                  bgcolor: (theme) => theme.palette.mode === 'dark' ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.05)',
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
                           color: active ? 'text.primary' : 'text.secondary',
                           bgcolor: active ? 'background.paper' : 'transparent',
                           boxShadow: active ? '0 1px 3px rgba(0,0,0,0.12)' : 'none',
                           '&:hover': { bgcolor: active ? 'background.paper' : 'action.hover' },
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
