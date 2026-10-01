import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import ListItemIcon from '@mui/material/ListItemIcon';
import ListItemText from '@mui/material/ListItemText';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Tooltip from '@mui/material/Tooltip';
import { alpha, type SxProps, type Theme } from '@mui/material/styles';
import MoreHorizIcon from '@mui/icons-material/MoreHoriz';

export interface ActionBarItem {
   /** Handler called when the action is clicked */
   onAction: () => void;
   /** MUI Icon component to display */
   icon: React.ReactElement;
   /** Translation key for the tooltip text (also used as the label) */
   tooltipKey: string;
   /** Optional translation key for a short button label; defaults to tooltipKey */
   labelKey?: string;
   /** Whether the action is disabled */
   disabled?: boolean;
   /** When true, the action is omitted from the bar entirely */
   hidden?: boolean;
   /** Show as a labelled button rather than inside the "More" menu */
   primary?: boolean;
   /** Destructive actions (e.g. delete) are shown in red and always live in the "More" menu */
   destructive?: boolean;
}

interface ActionBarProps {
   /** Array of action items to display */
   actions: ActionBarItem[];
   /** Number of labelled buttons when no action is marked primary */
   maxVisible?: number;
   /** Optional sx props for the container */
   sx?: SxProps<Theme>;
}

/**
 * Row actions in the Apple style: the most useful actions as small labelled pill buttons,
 * everything else (including destructive actions) tucked into a "More" menu.
 */
export const ActionBar: React.FC<ActionBarProps> = ({ actions, maxVisible = 2, sx }) => {
   const { t } = useTranslation();
   const [anchor, setAnchor] = React.useState<HTMLElement | null>(null);

   const shown = actions.filter((a) => !a.hidden);
   const anyPrimary = shown.some((a) => a.primary);
   const candidates = shown.filter((a) => !a.destructive);
   const visible = anyPrimary
	  ? candidates.filter((a) => a.primary)
	  : candidates.slice(0, maxVisible);
   const overflow = shown.filter((a) => !visible.includes(a));

   const run = (action: ActionBarItem) => {
	  setAnchor(null);
	  action.onAction();
   };

   return (
	  <Box
		 onClick={(e) => e.stopPropagation()}
		 sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 6, flexShrink: 0, ...sx }}
	  >
		 {visible.map((action, index) => (
			<Tooltip key={index} title={t(action.tooltipKey)}>
			   <span>
				  <Button
					 size="small"
					 onClick={action.onAction}
					 disabled={action.disabled}
					 startIcon={action.icon}
					 sx={{
						display: { xs: 'none', sm: 'inline-flex' },
						'& .MuiButton-startIcon > svg': { fontSize: 18 },
					 }}
				  >
					 {t(action.labelKey ?? action.tooltipKey)}
				  </Button>
			   </span>
			</Tooltip>
		 ))}

		 {(overflow.length > 0 || visible.length > 0) && (
			<Tooltip title={t('common.more', 'More')}>
			   <IconButton
				  size="small"
				  onClick={(e) => setAnchor(e.currentTarget)}
				  aria-label={t('common.more', 'More')}
				  sx={{
					 // On narrow screens the labelled buttons are hidden, so the menu holds everything.
					 display: overflow.length > 0 ? 'inline-flex' : { xs: 'inline-flex', sm: 'none' },
					 bgcolor: (theme) => alpha(theme.palette.text.primary, 0.06),
					 '&:hover': { bgcolor: (theme) => alpha(theme.palette.text.primary, 0.12) },
				  }}
			   >
				  <MoreHorizIcon fontSize="small" />
			   </IconButton>
			</Tooltip>
		 )}
		 <Menu
			anchorEl={anchor}
			open={Boolean(anchor)}
			onClose={() => setAnchor(null)}
			anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
			transformOrigin={{ vertical: 'top', horizontal: 'right' }}
			slotProps={{ paper: { sx: { minWidth: 220 } } }}
		 >
			{[...visible.map((a) => ({ a, mobileOnly: true })), ...overflow.map((a) => ({ a, mobileOnly: false }))]
			   .map(({ a, mobileOnly }, index) => (
				  <MenuItem
					 key={index}
					 onClick={() => run(a)}
					 disabled={a.disabled}
					 sx={{
						...(mobileOnly && { display: { xs: 'flex', sm: 'none' } }),
						...(a.destructive && { color: 'error.main', '& .MuiListItemIcon-root': { color: 'error.main' } }),
					 }}
				  >
					 <ListItemIcon sx={{ '& svg': { fontSize: 20 } }}>{a.icon}</ListItemIcon>
					 <ListItemText>{t(a.labelKey ?? a.tooltipKey)}</ListItemText>
				  </MenuItem>
			   ))}
		 </Menu>
	  </Box>
   );
};
