import * as React from 'react';
import { useTranslation } from 'react-i18next';

import {
   Box,
   Button,
   Card,
   CardActions,
   CardContent,
   List,
   ListItem,
   ListItemIcon,
   ListItemText,
   Typography
} from '@mui/material';

import CircleIcon from '@mui/icons-material/Circle';

/**
 * Represents a content block in the card body.
 * Can be a paragraph (string key) or a bullet list (array of string keys).
 */
export type ContentBlock =
   | { type: 'paragraph'; textKey: string }
   | { type: 'bullets'; items: string[] };

export interface WizardActionCardProps {
   /** Translation key for the card title */
   titleKey: string;
   /** Array of content blocks for the card body */
   content: ContentBlock[];
   /** Translation key for the button text */
   buttonKey: string;
   /** Click handler for the button */
   onButtonClick?: () => void;
   /** Optional: theme-aware background color for the button (e.g. 'primary.light') */
   buttonColor?: string;
   /** Optional: theme-aware text color for the button (e.g. 'primary.dark') */
   buttonTextColor?: string;
}

export const WizardActionCard: React.FC<WizardActionCardProps> = ({
   titleKey,
   content,
   buttonKey,
   onButtonClick,
   buttonColor,
   buttonTextColor
}) => {
   const { t } = useTranslation();

   return (
      <Card sx={{ height: '100%', display: 'flex', flexDirection: 'column' }} elevation={4}>
         <CardContent sx={{ flexGrow: 1 }}>
            <Typography variant="h6" component="h2" gutterBottom sx={{ fontWeight: 'bold' }}>
               {t(titleKey)}
            </Typography>
            <Box>
               {content.map((block, index) => {
                  if (block.type === 'paragraph') {
                     return (
                        <Typography
                           key={index}
                           variant="body2"
                           color="text.secondary"
                           sx={{ mb: 2 }} 
                        >
                           {t(block.textKey)}
                        </Typography>
                     );
                  } else if (block.type === 'bullets') {
                     return (
                        <List key={index} dense disablePadding sx={{ pl: 1 }}>
                           {block.items.map((itemKey, itemIndex) => (
                              <ListItem key={itemIndex} disablePadding sx={{ py: 0.25 }}>
                                 <ListItemIcon sx={{ minWidth: 24 }}>
                                    <CircleIcon sx={{ fontSize: 8 }} />
                                 </ListItemIcon>
                                 <ListItemText
                                    primary={<Typography variant='body2' color='text.secondary'>{t(itemKey)}</Typography>}
                                 />
                              </ListItem>
                           ))}
                        </List>
                     );
                  }
                  return null;
               })}
            </Box>
         </CardContent>
         <CardActions sx={{ p: 6, pt: 0, justifyContent: 'center' }}>
            <Button
               variant="contained"
               onClick={onButtonClick}
               sx={{
                  px: '30px',
                  borderRadius: '50px',
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

export default WizardActionCard;