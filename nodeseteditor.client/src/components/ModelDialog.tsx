import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Dialog from '@mui/material/Dialog';
import DialogTitle from '@mui/material/DialogTitle';
import DialogContent from '@mui/material/DialogContent';
import DialogActions from '@mui/material/DialogActions';
import Button from '@mui/material/Button';
import Box from '@mui/material/Box';
import IconButton from '@mui/material/IconButton';
import CloseIcon from '@mui/icons-material/Close';
import { useTheme } from '@mui/material/styles';

import { ContentLoader } from './ContentLoader';

export interface ModelDialogAction {
   label: string;
   onClick: () => void;
   variant?: 'text' | 'outlined' | 'contained';
   color?: 'inherit' | 'primary' | 'secondary' | 'success' | 'error' | 'info' | 'warning';
   disabled?: boolean;
}

interface ModelDialogProps {
   open: boolean;
   onClose: () => void;
   title: string;
   children?: React.ReactNode;
   actions?: ModelDialogAction[];
   isLoading?: boolean;
   isError?: boolean;
   error?: Error | null;
   maxWidth?: 'xs' | 'sm' | 'md' | 'lg' | 'xl' | false;
   fullWidth?: boolean;
}

export const ModelDialog: React.FC<ModelDialogProps> = ({
   open,
   onClose,
   title,
   children,
   actions = [],
   isLoading = false,
   isError = false,
   error = null,
   maxWidth = 'sm',
   fullWidth = true
}) => {
   const { t } = useTranslation();
   const theme = useTheme();

   return (
      <Dialog
         open={open}
         onClose={onClose}
         maxWidth={maxWidth}
         fullWidth={fullWidth}
         slotProps={{
            paper: {
               sx: {
                  border: `4px solid ${theme.palette.primary.main}`,
                  borderTop: 'none',
                  borderRadius: 2
               }
            }
         }}
      >
         <DialogTitle
            sx={{
               display: 'flex',
               alignItems: 'center',
               justifyContent: 'space-between',
               backgroundColor: theme.palette.primary.main,
               color: theme.palette.primary.contrastText,
               py: 1,
               minHeight: '52px'
            }}
         >
            {title}
            <IconButton
               aria-label="close"
               onClick={onClose}
               size="small"
               sx={{ color: theme.palette.primary.contrastText }}
            >
               <CloseIcon />
            </IconButton>
         </DialogTitle>
         <DialogContent sx={{ p: 0, m: 0 }}>
            <ContentLoader isLoading={isLoading} isError={isError} error={error}>
               {children}
            </ContentLoader>
         </DialogContent>
         <DialogActions sx={{ justifyContent: 'flex-start', px: 3, py: 2 }}>
            <Box sx={{ display: 'flex', gap: 1 }}>
               {actions.map((action, index) => (
                  <Button
                     key={index}
                     variant={action.variant ?? 'contained'}
                     color={action.color ?? 'primary'}
                     onClick={action.onClick}
                     disabled={action.disabled || isLoading}
                  >
                     {action.label}
                  </Button>
               ))}
               <Button
                  variant="text"
                  onClick={onClose}
                  disabled={isLoading}
               >
                  {t('common.cancel', 'Cancel')}
               </Button>
            </Box>
         </DialogActions>
      </Dialog>
   );
};
