import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Dialog from '@mui/material/Dialog';
import DialogTitle from '@mui/material/DialogTitle';
import DialogContent from '@mui/material/DialogContent';
import DialogActions from '@mui/material/DialogActions';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import { CloseIcon } from '../icons';

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

   return (
	  <Dialog
		 open={open}
		 onClose={onClose}
		 maxWidth={maxWidth}
		 fullWidth={fullWidth}
	  >
		 <DialogTitle
			sx={{
			   display: 'flex',
			   alignItems: 'center',
			   justifyContent: 'space-between',
			   gap: 8,
			   pr: 12,
			}}
		 >
			{title}
			<IconButton aria-label={t('common.close', 'Close')} onClick={onClose} size="small">
			   <CloseIcon fontSize="small" />
			</IconButton>
		 </DialogTitle>
		 {/* The dialog's gutter lives here, not in each caller. Callers add their own
		     p: 3–6 (3–6px, theme spacing is 1px) for internal rhythm, which on its own
		     left outlined inputs almost touching the dialog edge. 16px here brings every
		     dialog to a ~20px gutter, matching DialogTitle and DialogActions. */}
		 <DialogContent dividers sx={{ px: 16, py: 12, m: 0 }}>
			<ContentLoader isLoading={isLoading} isError={isError} error={error}>
			   {children}
			</ContentLoader>
		 </DialogContent>
		 <DialogActions>
			{/* Secondary (Cancel) on the left, primary action(s) on the right. */}
			<Button variant="outlined" onClick={onClose} disabled={isLoading}>
			   {t('common.cancel', 'Cancel')}
			</Button>
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
		 </DialogActions>
	  </Dialog>
   );
};
