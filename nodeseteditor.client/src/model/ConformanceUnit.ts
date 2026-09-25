/**
 * Conformance units are the NodeSet XML's <Category> elements, declared per node. This is the
 * NodeSet-wide view of them: one entry per distinct unit name, aggregated server-side.
 */
export interface ConformanceUnitInfo {
   name: string;
   /** How many nodes in the model declare this unit. */
   nodeCount: number;
}
