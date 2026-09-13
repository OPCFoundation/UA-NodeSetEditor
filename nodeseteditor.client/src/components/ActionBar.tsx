import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Box from '@mui/material/Box';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import type { SxProps, Theme } from '@mui/material';

export interface ActionBarItem {
   /** Handler called when the action is clicked */
   onAction: () => void;
   /** MUI Icon component to display */
   icon: React.ReactElement;
   /** Translation key for the tooltip text */
   tooltipKey: string;
   /** Whether the action is disabled */
   disabled?: boolean;
   /** When true, the action is omitted from the bar entirely */
   hidden?: boolean;
}

interface ActionBarProps {
   /** Array of action items to display */
   actions: ActionBarItem[];
   /** Optional sx props for the container */
   sx?: SxProps<Theme>;
}

export const ActionBar: React.FC<ActionBarProps> = ({ actions, sx }) => {
   const { t } = useTranslation();

   return (
      <Box
         sx={{
            display: 'flex',
            flexDirection: 'row',
            alignItems: 'center',
            backgroundColor: 'inherit',
            ...sx
         }}
      >
         {actions.filter((action) => !action.hidden).map((action, index) => (
            <Tooltip key={index} title={t(action.tooltipKey)}>
               <span>
                  <IconButton
                     onClick={action.onAction}
                     disabled={action.disabled}
                     size="small"
                  >
                     {action.icon}
                  </IconButton>
               </span>
            </Tooltip>
         ))}
      </Box>
   );
};
