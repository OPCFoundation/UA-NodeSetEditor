import * as React from 'react';

import AppBar from '@mui/material/AppBar';
import Box from '@mui/material/Box';
import Drawer from '@mui/material/Drawer';
import IconButton from '@mui/material/IconButton';
import { CloseIcon } from '../icons';

import { Footer } from './FooterMenu';
import { TopMenu } from './TopMenu';
import { type PageLayout } from '../AppRoutes';
import { HelpContext, HELP_SEARCH_URL } from '../HelpContext';
import { WorkspaceContext } from '../WorkspaceContext';
import { useResizableWidth } from '../hooks/useResizableWidth';

const SIDEBAR_MIN = 240;
const SIDEBAR_MAX = 720;

interface LayoutProps {
   page?: PageLayout | null
}

export const Layout = ({ page }: LayoutProps) => {
   const [mobileOpen, setMobileOpen] = React.useState(false);
   const { openHelp } = React.useContext(HelpContext);
   const { selectedType } = React.useContext(WorkspaceContext);
   const { width: sidebarWidth, isResizing, startResize, onHandleKeyDown, reset: resetWidth } =
      useResizableWidth('nodeseteditor.sidebarWidth', 340, SIDEBAR_MIN, SIDEBAR_MAX);

   if (!page) {
      return undefined;
   }

   const hasSidebar = !!page.sidebar;

   // Shared body so the desktop aside and the mobile Drawer render the
   // exact same React subtree — keeps internal state (expanded items,
   // focus toggle, etc.) consistent regardless of which surface is
   // currently visible.
   const sidebarBody = hasSidebar ? (
      <Box sx={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
         {page.sidebar}
      </Box>
   ) : null;

   const handleOpenHelp = () => {
      if (selectedType) {
         // Use the node's own documentation URL if present, otherwise search.
         openHelp(selectedType.documentation ?? HELP_SEARCH_URL(selectedType.displayName));
      } else {
         openHelp(); // default: https://reference.opcfoundation.org
      }
   };

   return (
      <Box sx={{
         display: 'flex',
         flexDirection: 'column',
         height: '100vh',
         width: '100vw',
         overflow: 'hidden',
      }}>
         <AppBar position="static" sx={{ zIndex: (theme) => theme.zIndex.drawer + 1 }}>
            <TopMenu
               title={'main.title'}
               onOpenSidebar={hasSidebar ? () => setMobileOpen(true) : undefined}
               onOpenHelp={handleOpenHelp}
            />
         </AppBar>
         <Box sx={{
            display: 'flex',
            flex: 1,
            overflow: 'hidden',
         }}>
            {hasSidebar && (
               <>
                  {/* Mobile drawer — fills the viewport. Visible only on
                      xs/sm; toggled by the hamburger in TopMenu. */}
                  <Drawer
                     variant="temporary"
                     open={mobileOpen}
                     onClose={() => setMobileOpen(false)}
                     ModalProps={{ keepMounted: true }}
                     sx={{
                        display: { xs: 'block', md: 'none' },
                        // The AppBar above is pinned at zIndex.drawer + 1
                        // (leftover from a permanent-drawer pattern), which
                        // would otherwise cover the top of this temporary
                        // drawer — including the close button. Push the
                        // drawer (modal root + paper) one step higher so
                        // it overlays the AppBar.
                        zIndex: (theme) => theme.zIndex.drawer + 2,
                        '& .MuiDrawer-paper': {
                           width: '100vw',
                           backgroundColor: (theme) => theme.palette.grey[200],
                        },
                     }}
                  >
                     <Box
                        sx={{
                           display: 'flex',
                           justifyContent: 'flex-end',
                           alignItems: 'center',
                           px: 1,
                           py: 0.5,
                           borderBottom: 1,
                           borderColor: 'divider',
                           minHeight: 40,
                        }}
                     >
                        <IconButton
                           onClick={() => setMobileOpen(false)}
                           size="small"
                           color="primary"
                           aria-label="close sidebar"
                        >
                           <CloseIcon fontSize="small" />
                        </IconButton>
                     </Box>
                     {sidebarBody}
                  </Drawer>

                  {/* Desktop static aside — md and up. Drag the right edge to resize. */}
				  <Box
					 component="aside"
					 sx={{
						width: `${sidebarWidth}px`,
						flexShrink: 0,
						position: 'relative',
						backgroundColor: (theme) => theme.palette.grey[200],
						borderRight: 1,
						borderColor: 'divider',
						display: { xs: 'none', md: 'flex' },
						flexDirection: 'column',
						overflow: 'hidden',
					 }}
				  >
					 {sidebarBody}
					 <Box
						role="separator"
						aria-orientation="vertical"
						aria-label="Resize sidebar"
						aria-valuenow={sidebarWidth}
						aria-valuemin={SIDEBAR_MIN}
						aria-valuemax={SIDEBAR_MAX}
						tabIndex={0}
						title="Drag to resize · double-click to reset"
						onPointerDown={startResize}
						onKeyDown={onHandleKeyDown}
						onDoubleClick={resetWidth}
						sx={{
						   position: 'absolute',
						   top: 0,
						   right: 0,
						   width: 8,
						   height: '100%',
						   cursor: 'col-resize',
						   zIndex: 1,
						   '&::after': {
							  content: '""',
							  position: 'absolute',
							  top: 0,
							  right: 0,
							  width: 3,
							  height: '100%',
							  backgroundColor: isResizing ? 'primary.main' : 'transparent',
							  transition: 'background-color 0.15s ease',
						   },
						   '&:hover::after, &:focus-visible::after': { backgroundColor: 'primary.main' },
						   outline: 'none',
						}}
					 />
				  </Box>
               </>
            )}
            <Box
               component="main"
               sx={{
                  flexGrow: 1,
                  p: { xs: 12, md: 24 },
                  overflowY: 'auto',
                  height: '100%',
                  backgroundColor: 'background.default',
                  minWidth: 0,
               }}
            >
               {page.main}
            </Box>
         </Box>
         <AppBar
            position="static"
            component="footer"
            sx={{ top: 'auto', bottom: 0, borderBottom: 0, borderTop: 1, borderColor: 'divider' }}
         >
            <Footer />
         </AppBar>
      </Box>
   );
}

export default Layout;
