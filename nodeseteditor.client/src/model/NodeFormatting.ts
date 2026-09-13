import type { LocalizedText } from './WorkspaceDescription';
import { formatBrowseName, formatNodeId } from '../utils/formatNodeId';

/** Resolve a NodeId to a display name using a lookup function, with formatted NodeId fallback */
export function resolveDisplayName(
   nodeId: string | undefined,
   nodeLookup: Map<string, { displayName?: LocalizedText; browseName?: string }>,
   nsMap?: Map<string, string>,
): string {
   if (!nodeId) return '';
   const node = nodeLookup.get(nodeId);
   if (node) {
      return node.displayName?.text ?? formatBrowseName(node.browseName, nsMap) ?? formatNodeId(nodeId, nsMap);
   }
   return formatNodeId(nodeId, nsMap);
}

/** Map modelling rule NodeId to abbreviation */
export function formatModellingRule(modellingRuleId?: string): string {
   switch (modellingRuleId) {
      case 'i=78': return 'M';
      case 'i=80': return 'O';
      case 'i=11508': return 'OP';
      case 'i=11510': return 'MP';
      default: return '';
   }
}

/** Suffix appended to a DataType name based on ValueRank. */
export function formatValueRankSuffix(valueRank?: number | null): string {
   if (valueRank === undefined || valueRank === null || valueRank === -1) return '';
   switch (valueRank) {
      case -2: return '[0..*]';
      case -3: return '[0..1]';
      case 0: return '[*]';
      case 1: return '[]';
      default: return valueRank > 1 ? '[' + ','.repeat(valueRank - 1) + ']' : '';
   }
}

/** Map ValueRank integer to a human-readable label. Named labels cover -3..2;
 *  ranks of 3 or more fall through to the raw number. */
export function formatValueRank(valueRank?: number): string {
   if (valueRank === undefined || valueRank === null) return '';
   switch (valueRank) {
      case -3: return 'ScalarOrOneDimension';
      case -2: return 'Any';
      case -1: return 'Scalar';
      case 0: return 'OneOrMoreDimensions';
      case 1: return 'OneDimension';
      case 2: return 'TwoDimension';
      default: return String(valueRank);
   }
}

/** Format a DataType + ValueRank + ArrayDimensions for display */
export function formatDataType(
   dataTypeId?: string,
   valueRank?: number,
   arrayDimensions?: string,
   nsMap?: Map<string, string>,
   nodeLookup?: Map<string, { displayName?: LocalizedText; browseName?: string }>,
): string {
   if (!dataTypeId) return '';

   let name: string;
   if (nodeLookup) {
      name = resolveDisplayName(dataTypeId, nodeLookup, nsMap);
   } else {
      name = formatNodeId(dataTypeId, nsMap);
   }

   if (valueRank !== undefined && valueRank >= 1) {
      if (arrayDimensions) {
         name += `[${arrayDimensions}]`;
      } else {
         name += '[]';
      }
   }

   return name;
}

/** Map NodeClass string enum to numeric value (for legacy components) */
export function nodeClassToNum(nc?: string): number {
   switch (nc) {
      case 'Object': return 1;
      case 'Variable': return 2;
      case 'Method': return 4;
      case 'ObjectType': return 8;
      case 'VariableType': return 16;
      case 'ReferenceType': return 32;
      case 'DataType': return 64;
      case 'View': return 128;
      default: return 0;
   }
}

/** Map numeric NodeClass to string enum */
export function numToNodeClass(nc: number): string {
   switch (nc) {
      case 1: return 'Object';
      case 2: return 'Variable';
      case 4: return 'Method';
      case 8: return 'ObjectType';
      case 16: return 'VariableType';
      case 32: return 'ReferenceType';
      case 64: return 'DataType';
      case 128: return 'View';
      default: return 'Object';
   }
}
