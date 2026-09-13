import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Box from '@mui/material/Box';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';

import { ModelDialog } from './ModelDialog';
import { NodeSelector } from './NodeSelector';

export interface AddFieldData {
   name: string;
   dataType: string;
   dataTypeId?: string;
   valueRank: number;
   arrayDimensions: string;
   description: string;
   isOptional?: boolean;
   allowSubTypes?: boolean;
   /** Int32 enumeration value; only meaningful for an Enumeration field. */
   value?: number;
}

export interface FieldDialogInitialData {
   name: string;
   dataTypeName: string;
   dataTypeId?: string;
   valueRank: number;
   arrayDimensions: string;
   description: string;
   isOptional?: boolean;
   allowSubTypes?: boolean;
   /** Int32 enumeration value; only meaningful for an Enumeration field. */
   value?: number;
}

interface AddFieldDialogProps {
   open: boolean;
   onClose: () => void;
   onAdd: (data: AddFieldData) => void;
   isAdding: boolean;
   addError: string | null;
   workspaceId: string;
   mode: 'add' | 'edit' | 'view';
   initialData?: FieldDialogInitialData;
   showOptionalCheckbox?: boolean;
   showAllowSubTypesCheckbox?: boolean;
   isMutuallyExclusive?: boolean;
   isStructureOrUnion?: boolean;
   /**
    * True for an Enumeration DataType field: the dialog collects only Name + Value (Int32) +
    * Description, hiding DataType / Value Rank / Array Dimensions which don't apply to enum fields.
    */
   isEnumeration?: boolean;
   /**
    * True for an OptionSet DataType field. Same shape as an Enumeration field, except the
    * value is a bit position in the underlying unsigned integer: labelled Bit, and bounded
    * to 0-63 (63 being the top bit of the widest UInteger).
    */
   isOptionSet?: boolean;
}

const valueRankOptions = [
   { value: -2, labelKey: 'typeDetail.valueRankAny' },
   { value: -1, labelKey: 'typeDetail.valueRankScalar' },
   { value: 0, labelKey: 'typeDetail.valueRankOneOrMoreDimensions' },
   { value: 1, labelKey: 'typeDetail.valueRankOneDimension' },
   { value: 2, labelKey: 'typeDetail.valueRankTwoDimension' },
   { value: -3, labelKey: 'typeDetail.valueRankScalarOrOneDimension' },
];

export const AddFieldDialog: React.FC<AddFieldDialogProps> = ({
   open,
   onClose,
   onAdd,
   isAdding,
   addError,
   workspaceId,
   mode,
   initialData,
   showOptionalCheckbox = false,
   showAllowSubTypesCheckbox = false,
   isMutuallyExclusive = false,
   isStructureOrUnion = false,
   isEnumeration = false,
   isOptionSet = false,
}) => {
   const { t } = useTranslation();
   const readOnly = mode === 'view';

   const [name, setName] = React.useState('');
   const [dataType, setDataType] = React.useState('');
   const [dataTypeId, setDataTypeId] = React.useState<string | undefined>(undefined);
   const [valueRank, setValueRank] = React.useState(-1);
   const [arrayDimensions, setArrayDimensions] = React.useState('');
   const [description, setDescription] = React.useState('');
   const [isOptional, setIsOptional] = React.useState(false);
   const [allowSubTypes, setAllowSubTypes] = React.useState(false);
   // Kept as a string so the field can be cleared/edited freely; parsed to Int32 on submit.
   const [value, setValue] = React.useState('');

   React.useEffect(() => {
      if (open) {
         if (initialData) {
            setName(initialData.name);
            setDataType(initialData.dataTypeName);
            setDataTypeId(initialData.dataTypeId);
            setValueRank(initialData.valueRank);
            setArrayDimensions(initialData.arrayDimensions);
            setDescription(initialData.description);
            setIsOptional(initialData.isOptional ?? false);
            setAllowSubTypes(initialData.allowSubTypes ?? false);
            setValue(initialData.value != null ? String(initialData.value) : '');
         } else {
            setName('');
            setDataType('');
            setDataTypeId(undefined);
            setValueRank(-1);
            setArrayDimensions('');
            setDescription('');
            setIsOptional(false);
            setAllowSubTypes(false);
            setValue('');
         }
      }
   }, [open, initialData]);

   // Enumeration and OptionSet fields are both Name + number + Description; the rest of the
   // form (DataType / Value Rank / Array Dimensions) only applies to Structure and Union.
   const isValueOnly = isEnumeration || isOptionSet;

   // A valid Int32 (whole number, optional leading sign). An OptionSet's number is a bit
   // position, so it additionally has to fall inside the widest UInteger.
   const trimmedValue = value.trim();
   const valueIsValidInt = /^-?\d+$/.test(trimmedValue)
      && (!isOptionSet || (Number(trimmedValue) >= 0 && Number(trimmedValue) <= 63));
   const valueError = isValueOnly && !readOnly && !!trimmedValue && !valueIsValidInt;

   const handleAdd = () => {
      onAdd({
         name, dataType, dataTypeId, valueRank, arrayDimensions, description,
         isOptional: showOptionalCheckbox ? isOptional : undefined,
         allowSubTypes: showAllowSubTypesCheckbox ? allowSubTypes : undefined,
         value: isValueOnly && valueIsValidInt ? Number(trimmedValue) : undefined,
      });
   };

   const handleOptionalChange = (checked: boolean) => {
      setIsOptional(checked);
      if (isMutuallyExclusive && checked) setAllowSubTypes(false);
   };

   const handleAllowSubTypesChange = (checked: boolean) => {
      setAllowSubTypes(checked);
      if (isMutuallyExclusive && checked) setIsOptional(false);
   };

   const title = mode === 'add'
      ? t('typeDetail.addField')
      : mode === 'edit'
         ? t('typeDetail.editField')
         : t('typeDetail.viewField');

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={title}
         isLoading={isAdding}
         isError={!!addError}
         error={addError ? new Error(addError) : null}
         actions={readOnly ? [] : [
            {
               label: t('common.ok'),
               onClick: handleAdd,
               disabled: !name.trim() || isAdding || (isValueOnly && !valueIsValidInt),
            },
         ]}
      >
         <Box sx={{ pt: 10, px: 6, pb: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
            <TextField
               label={t('typeDetail.fieldName')}
               value={name}
               onChange={(e) => setName(e.target.value)}
               required
               fullWidth
               size="small"
               autoFocus={!readOnly}
               placeholder={readOnly ? undefined : t('typeDetail.fieldNamePlaceholder')}
               slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
            />
            {isValueOnly ? (
               // Enumeration / OptionSet fields are Name + number + Description only — no
               // DataType, Value Rank or Array Dimensions.
               <TextField
                  label={isOptionSet ? t('typeDetail.fieldBit') : t('typeDetail.fieldValue')}
                  value={value}
                  onChange={(e) => setValue(e.target.value)}
                  required
                  fullWidth
                  size="small"
                  type={readOnly ? undefined : 'number'}
                  error={valueError}
                  helperText={
                     valueError
                        ? (isOptionSet ? t('typeDetail.fieldBitInvalid') : t('typeDetail.fieldValueInvalid'))
                        : isOptionSet && !readOnly
                           ? t('typeDetail.fieldBitHint')
                           : undefined}
                  placeholder={readOnly ? undefined
                     : isOptionSet ? t('typeDetail.fieldBitPlaceholder') : t('typeDetail.fieldValuePlaceholder')}
                  slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
               />
            ) : (
               <>
                  <NodeSelector
                     workspaceId={workspaceId}
                     nodeClass={64}
                     value={dataType}
                     onChange={setDataType}
                     onNodeIdChange={(id) => setDataTypeId(id || undefined)}
                     label={t('typeDetail.fieldDataType')}
                     placeholder={t('typeDetail.fieldDataTypePlaceholder')}
                     readOnly={readOnly}
                  />
                  <TextField
                     label={t('typeDetail.fieldValueRank')}
                     value={valueRank}
                     onChange={(e) => setValueRank(Number(e.target.value))}
                     select={!readOnly}
                     fullWidth
                     size="small"
                     slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
                  >
                     {!readOnly && valueRankOptions.map((opt) => (
                        <MenuItem key={opt.value} value={opt.value}>
                           {t(opt.labelKey)}
                        </MenuItem>
                     ))}
                  </TextField>
                  <TextField
                     label={t('typeDetail.fieldArrayDimensions')}
                     value={arrayDimensions}
                     onChange={(e) => setArrayDimensions(e.target.value)}
                     fullWidth
                     size="small"
                     placeholder={readOnly ? undefined : t('typeDetail.fieldArrayDimensionsPlaceholder')}
                     slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
                  />
               </>
            )}
            <TextField
               label={t('typeDetail.fieldDescription')}
               value={description}
               onChange={(e) => setDescription(e.target.value)}
               fullWidth
               size="small"
               multiline
               minRows={2}
               placeholder={readOnly ? undefined : t('typeDetail.fieldDescriptionPlaceholder')}
               slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
            />
            {isStructureOrUnion && showOptionalCheckbox && (
               <FormControlLabel
                  control={
                     <Checkbox
                        checked={isOptional}
                        onChange={(e) => handleOptionalChange(e.target.checked)}
                        disabled={readOnly}
                     />
                  }
                  label={t('typeDetail.isOptional')}
               />
            )}
            {isStructureOrUnion && showAllowSubTypesCheckbox && (
               <FormControlLabel
                  control={
                     <Checkbox
                        checked={allowSubTypes}
                        onChange={(e) => handleAllowSubTypesChange(e.target.checked)}
                        disabled={readOnly}
                     />
                  }
                  label={t('typeDetail.allowSubTypesCheckbox')}
               />
            )}
         </Box>
      </ModelDialog>
   );
};
