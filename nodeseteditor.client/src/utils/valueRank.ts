export interface ValueRankOption {
   value: number;
   labelKey: string;
}

export const allValueRankOptions: ValueRankOption[] = [
   { value: -2, labelKey: 'typeDetail.valueRankAny' },
   { value: -1, labelKey: 'typeDetail.valueRankScalar' },
   { value: 0, labelKey: 'typeDetail.valueRankOneOrMoreDimensions' },
   { value: 1, labelKey: 'typeDetail.valueRankOneDimension' },
   { value: 2, labelKey: 'typeDetail.valueRankTwoDimension' },
   { value: -3, labelKey: 'typeDetail.valueRankScalarOrOneDimension' },
];

/**
 * Returns the subset of ValueRank options allowed given the parent type's ValueRank.
 *
 *   Any (-2)                  → all options
 *   Scalar (-1)               → Scalar
 *   OneDimension (1)          → OneDimension
 *   TwoDimension (2)          → TwoDimension
 *   ScalarOrOneDimension (-3) → Scalar, OneDimension
 *   OneOrMoreDimensions (0)   → OneDimension, TwoDimension
 */
export function getAllowedValueRankOptions(parentValueRank: number | null | undefined): ValueRankOption[] {
   switch (parentValueRank ?? -2) {
      case -1: return allValueRankOptions.filter(o => o.value === -1);
      case 1:  return allValueRankOptions.filter(o => o.value === 1);
      case 2:  return allValueRankOptions.filter(o => o.value === 2);
      case -3: return allValueRankOptions.filter(o => o.value === -1 || o.value === 1);
      case 0:  return allValueRankOptions.filter(o => o.value === 1 || o.value === 2);
      case -2:
      default: return allValueRankOptions;
   }
}
