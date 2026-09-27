using System.Text;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Resolves a unit as written in a CSV ("degC", "bar", "psi") to an OPC UA EUInformation,
    /// against the two published mapping tables embedded in this assembly.
    ///
    /// There are two standard unit systems and they answer different questions:
    ///
    ///   * <b>IEC 62720</b> (CDD) — units used in engineering calculations. Checked first,
    ///     because that is what a tag list holds.
    ///   * <b>UNECE Recommendation 20</b> (UN/CEFACT) — units used for trade. Anything not
    ///     used to package something for sale is absent from it, so it is the fallback.
    ///
    /// Nothing about a unit is hardcoded here: the codes, UnitIds, display names and
    /// descriptions all come from the CSVs. What is written in code is only the handful of
    /// rules for reaching an official symbol from the ASCII a spreadsheet can hold — "degC"
    /// for "°C", "ohm" for "Ω" — see <see cref="Candidates"/>.
    /// </summary>
    public static class UnitLookup
    {
        /// <summary>Units used in engineering calculations (IEC 62720 / IEC CDD).</summary>
        public const string Iec62720NamespaceUri = "http://www.opcfoundation.org/UA/units/cdd/IEC62720";

        /// <summary>Units used for trade (UNECE Recommendation 20, UN/CEFACT).</summary>
        public const string UneceNamespaceUri = "http://www.opcfoundation.org/UA/units/un/cefact";

        // Embedded from Data\ in NodeSetEditor.Server.csproj. The logical name is the default
        // MSBuild assigns — root namespace plus the folder path — so moving or renaming the
        // files means changing these to match; Load says which names are actually present when
        // they don't.
        private const string Iec62720Resource = "NodeSetEditor.Server.Data.IEC62720_to_OPCUA.csv";
        private const string UneceResource = "NodeSetEditor.Server.Data.UNECE_to_OPCUA.csv";

        /// <summary>
        /// What goes into an EUInformation. On a miss the UnitId is 0, the DisplayName is the
        /// text as written, and <see cref="NamespaceUri"/> is empty — an unknown unit belongs
        /// to neither system, so claiming one would be a lie.
        /// </summary>
        public readonly record struct UnitInfo(
            int UnitId, string DisplayName, string? Description, string NamespaceUri)
        {
            public bool IsKnown => UnitId != 0;
        }

        /// <summary>
        /// One mapping table, indexed by display name, case-sensitively. There is no
        /// case-insensitive fallback: case carries the meaning of a unit symbol, and the SI
        /// prefixes make the difference enormous — "MV" is a megavolt and "mV" a millivolt,
        /// "Mg" a megagram and "mg" a milligram, "T" a tesla and "t" a tonne. Matching
        /// loosely would silently resolve an upper-cased export to a unit a million times out.
        /// </summary>
        private sealed class UnitTable
        {
            public required Dictionary<string, UnitInfo> Exact { get; init; }

            /// <summary>
            /// Display names more than one row claims that no rule in
            /// <see cref="DisambiguationRules"/> settles, so the first row in file order wins.
            /// Surfaced so a test can pin the set and fail when a table update adds to it.
            /// </summary>
            public required List<string> Unresolved { get; init; }
        }

        /// <summary>
        /// Which row to take when several claim the same display name: the display name, and
        /// the description of the entry meant by it.
        ///
        /// Keyed case-sensitively, like the tables themselves. The description is matched
        /// case-insensitively and exactly first, then by prefix — "pH" has to pick
        /// "pH (potential of hydrogen)" over "picohenry", and the two files disagree on
        /// whether Hydrogen is capitalized.
        ///
        /// <para>Everything here is a judgement about which unit a tag list means, not
        /// invented unit data: both candidates are real rows from the published tables.</para>
        /// </summary>
        private static readonly Dictionary<string, string> DisambiguationRules = new(StringComparer.Ordinal)
        {
            ["ms"] = "millisecond",                      // not the spin quantum number
            ["pH"] = "pH",                               // not picohenry
            ["rad"] = "radian",                          // not radiation, nor the absorbed-dose rad
            ["st"] = "stere",                            // not stone (UK)
            // The next four name a preference within UNECE for a display name IEC 62720 also
            // defines, unambiguously — so IEC decides and these are never consulted. Kept as
            // the recorded answer should a future table drop the IEC row.
            ["kvar"] = "kilovolt ampere (reactive)",     // not kilovar, and certainly not megavar
            ["mil"] = "mil",                             // not milli-inch
            ["MW"] = "megawatt",                         // not module width
            ["V"] = "volt DC",                           // not plain volt, nor volt AC
            // The same singular-vs-plural pair, spelled "rev/min" by IEC 62720 and "r/min" by
            // UNECE. One decision, so both spellings carry it.
            ["rev/min"] = "revolution per minute",
            ["r/min"] = "revolution per minute",
        };

        private static readonly Lazy<UnitTable> Iec62720 =
            new(() => Load(Iec62720Resource, Iec62720NamespaceUri));

        private static readonly Lazy<UnitTable> Unece =
            new(() => Load(UneceResource, UneceNamespaceUri));

        /// <summary>
        /// Display names in IEC 62720 that more than one row claims and nothing settles.
        /// </summary>
        public static IReadOnlyList<string> UnresolvedIec62720Collisions => Iec62720.Value.Unresolved;

        /// <summary>
        /// The same for UNECE Rec 20, excluding names IEC 62720 answers unambiguously: IEC
        /// decides those outright, so the UNECE rows for them are never consulted and an
        /// ambiguity among them is not something that needs resolving.
        /// </summary>
        public static IReadOnlyList<string> UnresolvedUneceCollisions =>
            Unece.Value.Unresolved.Where(name => !Iec62720.Value.Exact.ContainsKey(name)).ToList();

        /// <summary>
        /// Resolves a unit cell. IEC 62720 is exhausted before UNECE is consulted at all, so
        /// a unit present in both resolves to its engineering definition. Matching is
        /// case-sensitive throughout — see <see cref="UnitTable"/>.
        /// </summary>
        public static UnitInfo Resolve(string raw)
        {
            var asWritten = raw.Trim();
            var candidates = Candidates(asWritten);

            foreach (var table in new[] { Iec62720.Value, Unece.Value })
            {
                foreach (var candidate in candidates)
                {
                    if (table.Exact.TryGetValue(candidate, out var found)) return found;
                }
            }

            return new UnitInfo(0, asWritten, null, string.Empty);
        }

        /// <summary>
        /// The spellings to try, in order of preference. A spreadsheet holds ASCII, while the
        /// published display names are typographic ("°C", "Ω", "µm"), so the text as written
        /// is tried first and then the official symbol it most likely stands for.
        ///
        /// These are text rules, not unit knowledge — none of them names a UnitId or a code.
        /// </summary>
        private static List<string> Candidates(string asWritten)
        {
            var normalized = Normalize(asWritten);
            var candidates = new List<string> { normalized };

            void Add(string candidate)
            {
                if (candidate.Length > 0 && !candidates.Contains(candidate)) candidates.Add(candidate);
            }

            // "degC" / "deg C" / "degF" → "°C" / "°F". One rule rather than an entry per
            // temperature scale. The scale letter is also offered upper-cased, because "degc"
            // is a normal thing to type and "°c" is not a unit in either table — the lookup
            // itself stays case-sensitive, this just offers the spelling that exists.
            if (normalized.Length > 3 && normalized.StartsWith("deg", StringComparison.OrdinalIgnoreCase))
            {
                var scale = normalized[3..];
                Add("°" + scale);
                Add("°" + scale.ToUpperInvariant());
            }

            // Note what is deliberately absent: a bare "C" or "F" is left to mean what the
            // standards say — coulomb and farad — even though a tag list almost certainly
            // means a temperature. Re-pointing one valid unit at a different one is the kind
            // of invented unit knowledge these published tables exist to remove, and it would
            // be invisible in the result. "degC" and "°C" both reach degree Celsius.

            // "ohm" spelled out, for the symbol a keyboard can't reach.
            if (normalized.Equals("ohm", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("ohms", StringComparison.OrdinalIgnoreCase))
                Add("Ω");

            // "um"/"us" for "µm"/"µs": ASCII "u" standing in for the micro sign.
            if (normalized.Length > 1 && normalized[0] is 'u' or 'U') Add("µ" + normalized[1..]);

            return candidates;
        }

        /// <summary>
        /// Folds away the differences that are never meaningful in a unit symbol: surrounding
        /// and internal whitespace, underscores, and the Unicode characters a spreadsheet
        /// substitutes for their ASCII equivalents. Case is preserved — "mV" and "MV" are
        /// different units — and the degree sign is kept, because dropping it would turn "°C"
        /// into "C", which is the coulomb.
        /// </summary>
        private static string Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c) || c == '_') continue;
                sb.Append(c switch
                {
                    '‑' => '-',      // non-breaking hyphen, as used in "acre‑ft"
                    '–' or '—' => '-',  // en/em dash
                    'μ' => 'µ', // Greek small mu → micro sign
                    _ => c,
                });
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reads one embedded mapping table. Columns are code, UnitId, DisplayName,
        /// Description — the code itself is not kept: the UnitId already encodes it, and
        /// nothing here needs to render it.
        ///
        /// Rows are grouped by display name first, so a name several rows claim can be settled
        /// by <see cref="DisambiguationRules"/> rather than by whichever happened to be read
        /// first. A group no rule covers still falls back to file order, and its name is
        /// recorded in <see cref="UnitTable.Unresolved"/>.
        /// </summary>
        private static UnitTable Load(string resourceName, string namespaceUri)
        {
            var assembly = typeof(UnitLookup).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName)
                // Naming the resources that ARE embedded turns "the table moved" from a
                // puzzle into a one-line fix.
                ?? throw new InvalidOperationException(
                    $"Embedded unit table '{resourceName}' is missing. Embedded resources: "
                    + string.Join(", ", assembly.GetManifestResourceNames()));

            // Reuses the importer's own CSV reader, which already handles the BOM, CRLF and
            // the quoting these files use around names containing commas.
            var table = CsvTypeMapping.Parse(stream);

            var groups = new Dictionary<string, List<UnitInfo>>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var row in table.Rows)
            {
                var unitIdText = CsvTable.Cell(row, 1);
                var displayName = CsvTable.Cell(row, 2);
                var description = CsvTable.Cell(row, 3);

                if (displayName.Length == 0) continue;
                if (!int.TryParse(unitIdText, out var unitId) || unitId == 0) continue;

                var key = Normalize(displayName);
                if (key.Length == 0) continue;

                if (!groups.TryGetValue(key, out var candidates))
                {
                    groups[key] = candidates = new List<UnitInfo>();
                    order.Add(key);
                }

                // The same UnitId listed twice is a repeat, not an ambiguity.
                if (candidates.Any(c => c.UnitId == unitId)) continue;

                candidates.Add(new UnitInfo(
                    unitId, displayName,
                    description.Length == 0 ? null : description,
                    namespaceUri));
            }

            var exact = new Dictionary<string, UnitInfo>(StringComparer.Ordinal);
            var unresolved = new List<string>();

            // Walked in file order, so both the fallback choice and the recorded list are stable.
            foreach (var key in order)
            {
                var candidates = groups[key];
                if (candidates.Count == 1)
                {
                    exact[key] = candidates[0];
                    continue;
                }

                exact[key] = Disambiguate(key, candidates, out var settled);
                if (!settled) unresolved.Add(key);
            }

            return new UnitTable { Exact = exact, Unresolved = unresolved };
        }

        /// <summary>
        /// Picks between rows claiming the same display name, and says whether the choice was
        /// made deliberately or fell back to file order.
        ///
        /// Two things can settle a group. An explicit rule names the description to prefer —
        /// matched exactly first and then by prefix, so "mil" takes the row described as "mil"
        /// rather than "milli-inch" while "pH" still reaches "pH (potential of hydrogen)".
        /// Failing that, a group whose rows all describe the <i>same</i> unit under different
        /// codes is not an ambiguity at all, and the first row is simply taken.
        /// </summary>
        private static UnitInfo Disambiguate(string key, List<UnitInfo> candidates, out bool settled)
        {
            if (DisambiguationRules.TryGetValue(key, out var preferred))
            {
                foreach (var candidate in candidates)
                {
                    if (string.Equals(candidate.Description, preferred, StringComparison.OrdinalIgnoreCase))
                    {
                        settled = true;
                        return candidate;
                    }
                }

                foreach (var candidate in candidates)
                {
                    if (candidate.Description?.StartsWith(preferred, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        settled = true;
                        return candidate;
                    }
                }
            }

            // The same unit listed twice — "watt per square metre" under two codes, or "cord"
            // and "cord (128 ft3)". Either row is right, so take the first and don't report it
            // as needing a rule.
            settled = DescribeTheSameUnit(candidates);
            return candidates[0];
        }

        /// <summary>
        /// Whether every row in a group describes the same unit. Compared on the description
        /// with case, whitespace and parenthesised qualifiers folded away: a parenthetical
        /// names the same unit more precisely ("cord (128 ft3)") rather than naming a
        /// different one.
        /// </summary>
        private static bool DescribeTheSameUnit(List<UnitInfo> candidates)
        {
            var first = DescriptionKey(candidates[0].Description);
            if (first.Length == 0) return false;

            for (var i = 1; i < candidates.Count; i++)
            {
                if (DescriptionKey(candidates[i].Description) != first) return false;
            }
            return true;
        }

        private static string DescriptionKey(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return string.Empty;

            var sb = new StringBuilder(description.Length);
            var depth = 0;
            foreach (var c in description)
            {
                if (c == '(') { depth++; continue; }
                if (c == ')') { if (depth > 0) depth--; continue; }
                if (depth > 0 || char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
