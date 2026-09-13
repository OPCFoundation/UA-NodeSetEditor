import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';

import Box from '@mui/material/Box';
import IconButton from '@mui/material/IconButton';
import Toolbar from '@mui/material/Toolbar';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import HelpOutlineIcon from '@mui/icons-material/HelpOutline';
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

export const TopMenu = ({ title, onOpenSidebar, onOpenHelp }: TopMenuProps) => {
   const { t } = useTranslation();
   const navigate = useNavigate();
   return (
      <Toolbar disableGutters sx={{ py: 0, minHeight: '52px' }}>
         {onOpenSidebar && (
            <IconButton
               onClick={onOpenSidebar}
               color="inherit"
               sx={{ ml: 2, display: { xs: 'inline-flex', md: 'none' } }}
               aria-label={t('topMenu.openSidebar', 'Open sidebar')}
            >
               <MenuIcon />
            </IconButton>
         )}
         <Typography
            variant="h6"
            noWrap
            component="p"
            sx={{
               px: 6,
               display: { xs: 'flex', md: 'none' },
               flexGrow: 1
            }}
         >
            {t(title ?? '')}
         </Typography>
         <Box
            ml={6}
            my={0}
            pt={0}
            onClick={() => navigate('/')}
            title={t('topMenu.homeTooltip', 'Home')}
            sx={{ flexGrow: 0, display: { xs: 'none', md: 'flex' }, cursor: 'pointer' }}
         >
            <img src='/opclogo.png' alt='OPC Foundation' height={50} style={{
               borderTop: '1px solid white',
               borderLeft: '1px solid white',
               borderBottom: '1px solid black',
               borderRight: '1px solid black'
            }} />
         </Box>
         <Box ml={4} mr="auto" sx={{ flexGrow: 1, display: { xs: 'none', md: 'flex' } }}>
            <Box ml={2}>
               <Typography variant="h6" component="div" noWrap>
                  {t(title ??'')}
               </Typography>
            </Box>
         </Box>
         <Box sx={{ flexGrow: 0, display: 'flex', alignItems: 'center' }}>
            <Tooltip title={t('topMenu.helpTooltip', 'Help')}>
               <IconButton
                  color="inherit"
                  onClick={onOpenHelp}
                  aria-label={t('topMenu.helpTooltip', 'Help')}
                  sx={{ mr: 1 }}
               >
                  <HelpOutlineIcon />
               </IconButton>
            </Tooltip>
            <SettingsMenu />
         </Box>
      </Toolbar>
   );
}
