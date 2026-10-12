import * as React from 'react';

import Accordion from '@mui/material/Accordion';
import AccordionDetails from '@mui/material/AccordionDetails';
import AccordionSummary from '@mui/material/AccordionSummary';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';

import { ExpandMoreIcon } from '../icons';

interface DetailSectionProps {
   /** Stable id used for the persisted open/closed state. */
   id: string;
   title: string;
   expanded: boolean;
   /** Called with this section's id when the header is clicked. */
   onToggle: (id: string) => void;
   /**
    * Section-scoped view controls (e.g. a "hide inherited" filter), rendered as a
    * right-aligned row above the content.
    *
    * Not in the header: MUI renders an AccordionSummary as a <button>, so a control
    * placed there would be a button inside a button — invalid HTML, and React says so.
    * The content is also the only place these matter, since a collapsed section has
    * nothing to filter.
    */
   toolbar?: React.ReactNode;
   children?: React.ReactNode;
}

/**
 * One collapsible section of the node-detail view. Collapsed sections are unmounted
 * (not just hidden) so the queries that back them stay disabled until opened.
 */
export const DetailSection: React.FC<DetailSectionProps> = ({
   id, title, expanded, onToggle, toolbar, children,
}) => (
   <Accordion
      expanded={expanded}
      onChange={() => onToggle(id)}
      slotProps={{ transition: { unmountOnExit: true } }}
   >
      <AccordionSummary
         expandIcon={<ExpandMoreIcon fontSize="small" />}
         aria-controls={`${id}-content`}
         id={`${id}-header`}
      >
         <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>
            {title}
         </Typography>
      </AccordionSummary>
      <AccordionDetails id={`${id}-content`}>
         {toolbar && (
            <Box sx={{
               display: 'flex',
               justifyContent: 'flex-end',
               alignItems: 'center',
               gap: 2,
               px: 8,
               pt: 4,
            }}>
               {toolbar}
            </Box>
         )}
         {children}
      </AccordionDetails>
   </Accordion>
);
