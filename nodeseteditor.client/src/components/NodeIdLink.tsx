import * as React from 'react';
import Tooltip from '@mui/material/Tooltip';
import Link from '@mui/material/Link';
import { WorkspaceContext } from '../WorkspaceContext';

interface NodeIdLinkProps {
   /** Full NodeId for tooltip and navigation */
   nodeId?: string;
   /** BrowseName to display (already includes [ModelName]: prefix from server) */
   displayName?: string;
   /** NodeClass numeric value for navigation target */
   nodeClass?: number;
   /** If true, render as text with tooltip only (no clickable link) */
   noLink?: boolean;
}

/**
 * Displays a BrowseName as a clickable link.
 * Hover shows the full NodeId.
 * Click navigates to the type detail page.
 */
export const NodeIdLink: React.FC<NodeIdLinkProps> = ({ nodeId, displayName, nodeClass, noLink }) => {
   const { setSelectedType } = React.useContext(WorkspaceContext);

   // Guard against LocalizedText objects leaking through as displayName
   const label = typeof displayName === 'object' && displayName !== null
      ? (displayName as unknown as { text?: string }).text ?? ''
      : displayName;

   if (!label) return null;
   if (!nodeId) return <>{label}</>;

   if (noLink) {
      return (
         <Tooltip title={nodeId}>
            <span>{label}</span>
         </Tooltip>
      );
   }

   const handleClick = (e: React.MouseEvent) => {
      e.preventDefault();
      e.stopPropagation();
      setSelectedType({
         nodeId,
         displayName: label,
         nodeClass: nodeClass ?? 0,
      });
   };

   return (
      <Tooltip title={nodeId}>
         <Link
            component="button"
            variant="body2"
            underline="hover"
            onClick={handleClick}
            sx={{ textAlign: 'left' }}
         >
            {label}
         </Link>
      </Tooltip>
   );
};
