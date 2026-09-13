import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';

import Box from '@mui/material/Box';
import Grid from '@mui/material/Grid';
import Alert from '@mui/material/Alert';
import Typography from '@mui/material/Typography';

import { WizardActionCard, type ContentBlock } from '../components/WizardActionCard';

const aboutModelLibraryContent: ContentBlock[] = [
    { type: 'paragraph', textKey: 'aboutModelLibraryWizard.intro' },
    { type: 'paragraph', textKey: 'aboutModelLibraryWizard.line1' },
    { type: 'paragraph', textKey: 'aboutModelLibraryWizard.line2' },
    { type: 'paragraph', textKey: 'aboutModelLibraryWizard.line3' },
    { type: 'paragraph', textKey: 'aboutModelLibraryWizard.line4' }
];

const aboutTypeLibraryContent: ContentBlock[] = [
    { type: 'paragraph', textKey: 'aboutTypeLibraryWizard.intro' }
];

const aboutModelValidationContent: ContentBlock[] = [
   { type: 'paragraph', textKey: 'aboutModelValidationWizard.intro' }
];

const WelcomeWizardPage: React.FC = () => {
   const { t } = useTranslation();
   const navigate = useNavigate();

   return (
      <Box p={4}>
         <Typography variant='h5' sx={{ fontWeight: 'bolder' }}>
            {t('welcomeWizard.title')}
         </Typography>
           <Typography variant='body1' my={2} pb={4} sx={{ fontSize: 'smaller', fontWeight: 'lighter' }}>
            {t('welcomeWizard.summary')}
         </Typography>

         {/* Beta disclaimer */}
         <Alert severity="warning" sx={{ mb: 10 }}>
            This site is currently in a public beta. Any models created should be backed up using the download feature.
            Please help us make this site better by reporting any bugs, feature requests, or other feedback to{' '}
            <a href="mailto:webmaster@opcfoundation.org?subject=OPC%20UA%20NodeSetEditor%20Feedback">webmaster@opcfoundation.org</a>.
         </Alert>

           <Grid container spacing={6} sx={{ mt: 4 }}>
               <Grid size={{ xs: 12, md: 4 }}>
                   <WizardActionCard
                       titleKey="aboutTypeLibraryWizard.title"
                       content={aboutTypeLibraryContent}
                       buttonKey="aboutTypeLibraryWizard.action"
                       onButtonClick={() => navigate('/type_library')}
                       buttonColor="primary.dark"
                   />
               </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
               <WizardActionCard
                  titleKey="aboutModelLibraryWizard.title"
                  content={aboutModelLibraryContent}
                  buttonKey="aboutModelLibraryWizard.action"
                  onButtonClick={() => navigate('/model_library')}
                  buttonColor="primary.dark"
               />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
               <WizardActionCard
                  titleKey="aboutModelValidationWizard.title"
                  content={aboutModelValidationContent}
                  buttonKey="aboutModelValidationWizard.action"
                  onButtonClick={() => navigate('/model_library')}
                  buttonColor="primary.dark"
               />
            </Grid>
         </Grid>
      </Box>
   );
};

export default WelcomeWizardPage;
