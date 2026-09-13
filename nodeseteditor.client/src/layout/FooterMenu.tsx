import Box from '@mui/material/Box';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';

import { BuildVersion } from '../version.ts';
import { FooterLinks } from './FooterLinks.tsx';

export const Footer = () => {
   return (
      <Toolbar variant='dense' disableGutters sx={{ py: 0, minHeight: '36px', justifyContent: 'space-between' }}>
         <Box ml={6} sx={{ flexGrow: 0, display: { xs: 'none', color: 'red', md: 'flex' } }}>
            <FooterLinks />
         </Box>
         <Box
            alignContent='right'
            textAlign='right'
            mx={6}
            sx={{
               display: 'flex',
               alignItems: 'center'
            }}
         >
            <Typography variant='body2'>{BuildVersion}</Typography>
         </Box>
      </Toolbar>
   );
}
