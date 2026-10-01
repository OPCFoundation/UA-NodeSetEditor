import ChevronRightIcon from '@mui/icons-material/ChevronRight';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import type { SxProps, Theme } from '@mui/material/styles';

/** Chevron that points right when collapsed and down when expanded (UA Edge Translator style). */
export const treeSlots = { expandIcon: ChevronRightIcon, collapseIcon: ExpandMoreIcon };

/** Row layout for SimpleTreeView: compact rows and a plain toggle; the label carries the hover/selection pill. */
export const treeSx: SxProps<Theme> = {
   '& .MuiTreeItem-content': {
	  py: 1,
	  px: 2,
	  gap: 4,
	  borderRadius: '6px',
	  backgroundColor: 'transparent !important',
   },
   '& .MuiTreeItem-iconContainer': {
	  width: 20,
	  height: 20,
	  justifyContent: 'center',
	  borderRadius: '4px',
	  color: 'text.secondary',
	  '&:hover': { backgroundColor: 'action.hover', color: 'text.primary' },
   },
   '& .MuiTreeItem-label': { fontSize: 15, minWidth: 0 },
   '& .MuiTreeItem-groupTransition': { ml: 12, pl: 0 },
};

/** Label pill: hover tint, accent fill when selected, bold for the active model's namespace. */
export const treeLabelSx = (selected: boolean, emphasized: boolean, dimmed: boolean): SxProps<Theme> => ({
   display: 'flex',
   alignItems: 'center',
   gap: 6,
   minWidth: 0,
   cursor: 'pointer',
   '& .tree-label-text': {
	  fontSize: 15,
	  px: 6,
	  py: 2,
	  borderRadius: '6px',
	  whiteSpace: 'nowrap',
	  overflow: 'hidden',
	  textOverflow: 'ellipsis',
	  fontWeight: emphasized ? 700 : 400,
	  color: selected ? '#fff' : dimmed ? 'text.disabled' : 'text.primary',
	  backgroundColor: selected ? 'primary.main' : 'transparent',
   },
   '&:hover .tree-label-text': selected ? {} : { backgroundColor: 'action.hover' },
});
