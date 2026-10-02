import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';

import Box from '@mui/material/Box';
import Grid from '@mui/material/Grid';
import Alert from '@mui/material/Alert';
import Typography from '@mui/material/Typography';
import { CategoryIcon as CategoryOutlinedIcon, FolderIcon as FolderOutlinedIcon, TaskAltIcon as TaskAltOutlinedIcon, UploadFileIcon } from '../icons';

import { WizardActionCard, type ContentBlock } from '../components/WizardActionCard';
import { InfoPopover } from '../components/InfoPopover';
import { ImportCsvFlow } from '../components/ImportCsvFlow';

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

// What used to be the CSV wizard's opening page. The wizard now starts at the column
// mapping, so the explanation of what an import does belongs here, where the decision to
// start one is made.
const aboutCsvImportContent: ContentBlock[] = [
   { type: 'paragraph', textKey: 'csvImport.pickHint' }
];

const WelcomeWizardPage: React.FC = () => {
   const { t } = useTranslation();
   const navigate = useNavigate();

   return (
      <Box sx={{ maxWidth: 1100, mx: 'auto', py: { xs: 16, md: 48 } }}>
         <Box sx={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <Typography variant='h3' component='h1'>
               {t('welcomeWizard.title')}
            </Typography>
            <InfoPopover title={t('welcomeWizard.title')} label={t('common.learnMore', 'Learn more')}>
               {t('welcomeWizard.summary')}
            </InfoPopover>
         </Box>
         <Typography variant='h6' component='p' color='text.secondary' sx={{ mt: 8, fontWeight: 400 }}>
            {t('welcomeWizard.tagline')}
         </Typography>

         {/* Beta disclaimer */}
         {/* A beta warning, tinted amber as it was before the restyle — `outlined` with a
             divider border made the most prominent notice in the app look like plain chrome. */}
         <Alert severity="warning" sx={{ mt: 24 }}>
            Public beta — please back up your models with Download. Feedback:{' '}
            <a href="mailto:webmaster@opcfoundation.org?subject=OPC%20UA%20NodeSetEditor%20Feedback">webmaster@opcfoundation.org</a>
         </Alert>

         {/* Four cards at md: 6 — a 2x2 grid. */}
         <Grid container spacing={20} sx={{ mt: 32 }}>
            <Grid size={{ xs: 12, md: 6 }}>
               <WizardActionCard
                  icon={<CategoryOutlinedIcon />}
                  titleKey="aboutTypeLibraryWizard.title"
                  summaryKey="aboutTypeLibraryWizard.summary"
                  content={aboutTypeLibraryContent}
                  buttonKey="aboutTypeLibraryWizard.action"
                  onButtonClick={() => navigate('/type_library')}
               />
            </Grid>
            <Grid size={{ xs: 12, md: 6 }}>
               <WizardActionCard
                  icon={<FolderOutlinedIcon />}
                  titleKey="aboutModelLibraryWizard.title"
                  summaryKey="aboutModelLibraryWizard.summary"
                  content={aboutModelLibraryContent}
                  buttonKey="aboutModelLibraryWizard.action"
                  onButtonClick={() => navigate('/model_library')}
               />
            </Grid>
            <Grid size={{ xs: 12, md: 6 }}>
               <WizardActionCard
                  icon={<TaskAltOutlinedIcon />}
                  titleKey="aboutModelValidationWizard.title"
                  summaryKey="aboutModelValidationWizard.summary"
                  content={aboutModelValidationContent}
                  buttonKey="aboutModelValidationWizard.action"
                  onButtonClick={() => navigate('/model_library')}
               />
            </Grid>
            <Grid size={{ xs: 12, md: 6 }}>
               {/* The flow owns the sign-in / create-model / file-picker steps that have to
                   happen before the wizard can open, and hands the card a start(). */}
               <ImportCsvFlow>
                  {({ start, busy }) => (
                     <WizardActionCard
                        icon={<UploadFileIcon />}
                        titleKey="aboutCsvImportWizard.title"
                        summaryKey="aboutCsvImportWizard.summary"
                        content={aboutCsvImportContent}
                        buttonKey="aboutCsvImportWizard.action"
                        onButtonClick={start}
                        buttonDisabled={busy}
                     />
                  )}
               </ImportCsvFlow>
            </Grid>
         </Grid>
      </Box>
   );
};

export default WelcomeWizardPage;
