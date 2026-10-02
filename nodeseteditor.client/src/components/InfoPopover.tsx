import * as React from 'react';

import Box from '@mui/material/Box';
import IconButton from '@mui/material/IconButton';
import Popover from '@mui/material/Popover';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import { InfoOutlinedIcon } from '../icons';

export interface InfoPopoverProps {
   /** Optional heading shown at the top of the popover. */
   title?: React.ReactNode;
   /** Explanatory content. Strings render as paragraphs; arrays render one paragraph per entry. */
   children: React.ReactNode | string[];
   /** Accessible label and tooltip for the trigger button. */
   label?: string;
   size?: 'small' | 'medium';
}

/**
 * A small (i) button that reveals explanatory text in a popover. Keeps pages uncluttered
 * for newcomers: the essentials stay visible, the "why" is one click away.
 */
export const InfoPopover: React.FC<InfoPopoverProps> = ({ title, children, label = 'Learn more', size = 'small' }) => {
   const [anchor, setAnchor] = React.useState<HTMLElement | null>(null);
   const id = React.useId();

   const body = Array.isArray(children)
      ? children.map((text, i) => (
         <Typography key={i} variant="body2" color="text.secondary" sx={{ mb: i < children.length - 1 ? 10 : 0 }}>
            {text}
         </Typography>
      ))
      : typeof children === 'string'
         ? <Typography variant="body2" color="text.secondary">{children}</Typography>
         : children;

   return (
      <>
         <Tooltip title={label}>
            <IconButton
               size={size}
               aria-label={label}
               aria-describedby={anchor ? id : undefined}
               onClick={(e) => { e.stopPropagation(); setAnchor(e.currentTarget); }}
               sx={{ color: 'text.secondary', '&:hover': { color: 'primary.main' } }}
            >
               <InfoOutlinedIcon fontSize={size === 'small' ? 'small' : 'medium'} />
            </IconButton>
         </Tooltip>
         <Popover
            id={id}
            open={!!anchor}
            anchorEl={anchor}
            onClose={() => setAnchor(null)}
            anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
            transformOrigin={{ vertical: 'top', horizontal: 'center' }}
            slotProps={{ paper: { sx: { mt: 6, maxWidth: 360 } } }}
         >
            <Box sx={{ p: 16 }}>
               {title && (
                  <Typography variant="subtitle2" sx={{ mb: 8 }}>
                     {title}
                  </Typography>
               )}
               {body}
            </Box>
         </Popover>
      </>
   );
};

export default InfoPopover;
