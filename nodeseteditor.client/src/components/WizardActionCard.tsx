import * as React from 'react';
import { useTranslation } from 'react-i18next';

import {
   Box,
   Button,
   Card,
   CardActions,
   CardContent,
   Typography
} from '@mui/material';

import { InfoPopover } from './InfoPopover';

/**
 * Represents a content block in the card's "learn more" popover.
 * Can be a paragraph (string key) or a bullet list (array of string keys).
 */
export type ContentBlock =
   | { type: 'paragraph'; textKey: string }
   | { type: 'bullets'; items: string[] };

export interface WizardActionCardProps {
   /** Translation key for the card title */
   titleKey: string;
   /** Translation key for the one-line summary shown on the card */
   summaryKey?: string;
   /** Detailed explanation, shown in a popover behind the (i) button */
   content: ContentBlock[];
   /** Translation key for the button text */
   buttonKey: string;
   /** Click handler for the button */
   onButtonClick?: () => void;
   /** Optional icon shown above the title */
   icon?: React.ReactNode;
   /** Optional: theme-aware background color for the button (e.g. 'primary.light') */
   buttonColor?: string;
   /** Optional: theme-aware text color for the button (e.g. 'primary.dark') */
   buttonTextColor?: string;
   /** Optional: disables the button, e.g. while the action it starts is still resolving. */
   buttonDisabled?: boolean;
}

export const WizardActionCard: React.FC<WizardActionCardProps> = ({
   titleKey,
   summaryKey,
   content,
   buttonKey,
   onButtonClick,
   icon,
   buttonColor,
   buttonTextColor,
   buttonDisabled
}) => {
   const { t } = useTranslation();

   const details = (
      <Box>
         {content.map((block, index) => block.type === 'paragraph'
            ? (
               <Typography key={index} variant="body2" color="text.secondary" sx={{ mb: 8 }}>
                  {t(block.textKey)}
               </Typography>
            )
            : (
               <Box component="ul" key={index} sx={{ m: 0, mb: 8, pl: 18 }}>
                  {block.items.map((itemKey, itemIndex) => (
                     <Typography component="li" key={itemIndex} variant="body2" color="text.secondary">
                        {t(itemKey)}
                     </Typography>
                  ))}
               </Box>
            ))}
      </Box>
   );

   return (
      <Card
         sx={{
            height: '100%',
            display: 'flex',
            flexDirection: 'column',
            '&:hover': { transform: 'translateY(-2px)', boxShadow: '0 8px 30px rgba(0,0,0,0.08)' }
         }}
      >
         <CardContent sx={{ flexGrow: 1, p: 24 }}>
            {icon && (
               <Box sx={{ color: 'primary.main', mb: 12, '& svg': { fontSize: 36 } }}>
                  {icon}
               </Box>
            )}
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 4 }}>
               <Typography variant="h6" component="h2" sx={{ flexGrow: 1 }}>
                  {t(titleKey)}
               </Typography>
               {content.length > 0 && (
                  <InfoPopover title={t(titleKey)} label={t('common.learnMore', 'Learn more')}>
                     {details}
                  </InfoPopover>
               )}
            </Box>
            {summaryKey && (
               <Typography variant="body2" color="text.secondary" sx={{ mt: 6 }}>
                  {t(summaryKey)}
               </Typography>
            )}
         </CardContent>
         <CardActions sx={{ px: 24, pb: 24, pt: 0 }}>
            <Button
               variant="contained"
               onClick={onButtonClick}
               disabled={buttonDisabled}
               sx={{
                  ...(buttonColor && { bgcolor: buttonColor }),
                  ...(buttonTextColor && { color: buttonTextColor })
               }}
            >
               {t(buttonKey)}
            </Button>
         </CardActions>
      </Card>
   );
};
