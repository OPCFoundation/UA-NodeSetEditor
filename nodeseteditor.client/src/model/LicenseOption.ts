/**
 * A selectable license served by GET /opcua/v1/licenses to drive the
 * data-driven license selector. The single entry with isCustom=true is the
 * "Other / Proprietary" choice that lets the user supply a custom identifier
 * and a reference URL.
 */
export interface LicenseOption {
   spdxId: string;
   name: string;
   referenceUrl?: string | null;
   isCustom: boolean;
   sortOrder: number;
}
