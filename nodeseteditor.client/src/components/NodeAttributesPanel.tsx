import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableRow from '@mui/material/TableRow';
import Typography from '@mui/material/Typography';

import type { Node } from '../model/Node';

interface NodeAttributesPanelProps {
   node: Node;
}

/**
 * Presentational read-only attribute table for a Node. Used by both
 * ViewNodeDialog and NodePickerDialog so the row set, labels, and styling
 * stay in one place. Data fetching is the caller's job.
 */
export const NodeAttributesPanel: React.FC<NodeAttributesPanelProps> = ({ node }) => {
   const { t } = useTranslation();
   return (
      <Table size="small">
         <TableBody>
            <Row label={t('viewNode.nodeId', 'NodeId')} value={node.nodeId} />
            <Row label={t('viewNode.nodeClass', 'NodeClass')} value={node.nodeClass} />
            <Row label={t('viewNode.browseName', 'BrowseName')} value={node.browseName} />
            <Row label={t('viewNode.displayName', 'DisplayName')} value={node.displayName?.text} />
            <Row label={t('viewNode.description', 'Description')} value={node.description?.text} />
            {node.dataType && (
               <Row
                  label={t('viewNode.dataType', 'DataType')}
                  value={node.dataTypeName ?? node.dataType}
               />
            )}
            {node.valueRank !== undefined && node.valueRank !== null && (
               <Row label={t('viewNode.valueRank', 'ValueRank')} value={String(node.valueRank)} />
            )}
            {node.typeDefinition && (
               <Row
                  label={t('viewNode.typeDefinition', 'TypeDefinition')}
                  value={node.typeDefinitionName ?? node.typeDefinition}
               />
            )}
            {node.modellingRule && (
               <Row label={t('viewNode.modellingRule', 'ModellingRule')} value={node.modellingRule} />
            )}
            {node.parentNodeId && (
               <Row label={t('viewNode.parent', 'Parent')} value={node.parentNodeId} />
            )}
         </TableBody>
      </Table>
   );
};

const Row: React.FC<{ label: string; value?: string | null }> = ({ label, value }) => {
   if (!value) return null;
   return (
      <TableRow>
         <TableCell sx={{ fontWeight: 'bold', width: 160, verticalAlign: 'top' }}>
            <Typography variant="body2">{label}</Typography>
         </TableCell>
         <TableCell>
            <Typography variant="body2" sx={{ wordBreak: 'break-word' }}>{value}</Typography>
         </TableCell>
      </TableRow>
   );
};
