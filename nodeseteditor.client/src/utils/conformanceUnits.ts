/**
 * Conformance units — the <Category> elements of the NodeSet XML — are a list of
 * names, edited as free text with one name per line. Splitting on lines rather than
 * commas keeps names that contain a comma intact.
 */

/** Text box → list. Blank lines are dropped, so a trailing newline is not a unit. */
export function parseConformanceUnits(text: string): string[] {
   return text
      .split('\n')
      .map((unit) => unit.trim())
      .filter((unit) => unit.length > 0);
}

/** List → text box. */
export function formatConformanceUnits(units?: string[] | null): string {
   return (units ?? []).join('\n');
}
