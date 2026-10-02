import * as React from 'react';

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';

import { InfoPopover } from './InfoPopover';

export interface PageHeaderProps {
   title: React.ReactNode;
   icon?: React.ReactNode;
   /** Explanation shown behind the (i) button instead of inline on the page. */
   info?: React.ReactNode | string[];
   infoLabel?: string;
   /** Optional leading element, e.g. a back button. */
   leading?: React.ReactNode;
   onTitleClick?: () => void;
   /** Actions aligned to the right. */
   children?: React.ReactNode;
}

/** Minimal page title row: optional icon, large title, an (i) popover for help, and right-aligned actions. */
export const PageHeader: React.FC<PageHeaderProps> = ({ title, icon, info, infoLabel, leading, onTitleClick, children }) => (
   <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 8, mb: 16 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 8, flexGrow: 1, minWidth: 0 }}>
         {leading}
         <Box
            onClick={onTitleClick}
            sx={{
               display: 'flex',
               alignItems: 'center',
               gap: 8,
               minWidth: 0,
               ...(onTitleClick && { cursor: 'pointer', '&:hover': { color: 'primary.main' } })
            }}
         >
            {icon && <Box sx={{ display: 'flex', color: 'primary.main', '& svg': { fontSize: 28 } }}>{icon}</Box>}
            <Typography variant="h4" component="h1" noWrap sx={{ fontSize: { xs: '1.5rem', md: '1.875rem' } }}>
               {title}
            </Typography>
         </Box>
         {info && (
            <InfoPopover title={title} label={infoLabel}>
               {info}
            </InfoPopover>
         )}
      </Box>
      {children && (
         <Box sx={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            {children}
         </Box>
      )}
   </Box>
);

export default PageHeader;
