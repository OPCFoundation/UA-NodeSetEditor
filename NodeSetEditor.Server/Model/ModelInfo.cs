using System.Text.RegularExpressions;

namespace NodeSetEditor.Server.Model
{
    public class ModelInfo
    {
        public Guid? Id { get; set; }

        public string? ModelUri { get; set; }

        public string? PublicationDate { get; set; }

        public string? ModelVersion { get; set; }

        public string? Name { get; set; }

        public string? Path { get; set; }

        public string? Description { get; set; }

        /// <summary>
        /// True when the model failed to load into the address space
        /// (e.g. parse error, missing dependencies). The model still
        /// exists in workspace storage and can be removed.
        /// </summary>
        public bool HasErrors { get; set; }

        /// <summary>
        /// Local part of the publisher's email (domain stripped). Set when the model
        /// is published; shown in the shared-model picker. Null for unpublished models.
        /// </summary>
        public string? Creator { get; set; }

        /// <summary>
        /// License identifier (SPDX id or a custom "LicenseRef-…" id). Set once at model
        /// genesis and immutable thereafter.
        /// </summary>
        public string? License { get; set; }

        /// <summary>Reference URL for the license (required for custom/"Other" licenses).</summary>
        public string? LicenseUrl { get; set; }

        /// <summary>Copyright holder (e.g. "OPC Foundation, Inc."). Set once at genesis.</summary>
        public string? CopyrightHolder { get; set; }

        /// <summary>
        /// Represents a parsed semantic version for comparison.
        /// </summary>
        public class SemVer : IComparable<SemVer>
        {
            public int Major { get; set; }
            public int Minor { get; set; }
            public int Patch { get; set; }
            public string? Prerelease { get; set; }

            public int CompareTo(SemVer? other)
            {
                if (other == null) return 1;

                int result = Major.CompareTo(other.Major);
                if (result != 0) return result;

                result = Minor.CompareTo(other.Minor);
                if (result != 0) return result;

                result = Patch.CompareTo(other.Patch);
                if (result != 0) return result;

                // Prerelease versions have lower precedence than normal versions
                if (string.IsNullOrEmpty(Prerelease) && !string.IsNullOrEmpty(other.Prerelease))
                    return 1;
                if (!string.IsNullOrEmpty(Prerelease) && string.IsNullOrEmpty(other.Prerelease))
                    return -1;
                if (!string.IsNullOrEmpty(Prerelease) && !string.IsNullOrEmpty(other.Prerelease))
                    return string.Compare(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);

                return 0;
            }

            public bool SameMajorMinor(SemVer? other)
            {
                if (other == null) return false;
                return Major == other.Major && Minor == other.Minor;
            }

            public bool SameMajor(SemVer? other)
            {
                if (other == null) return false;
                return Major == other.Major;
            }

            public override string ToString()
            {
                var version = $"{Major}.{Minor}.{Patch}";
                if (!string.IsNullOrEmpty(Prerelease))
                    version += $"-{Prerelease}";
                return version;
            }
        }

        /// <summary>
        /// Parses a version string into a SemVer object.
        /// Handles non-standard formats like "1.0", "1.00.03", "1.01", etc.
        /// </summary>
        public static SemVer? ParseVersion(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return null;

            // Remove leading/trailing whitespace
            version = version.Trim();

            // Extract prerelease suffix if present (e.g., "1.0.0-beta")
            string? prerelease = null;
            var dashIndex = version.IndexOf('-');
            if (dashIndex > 0)
            {
                prerelease = version[(dashIndex + 1)..];
                version = version[..dashIndex];
            }

            // Split by dots
            var parts = version.Split('.');

            // Parse each part, removing leading zeros
            int major = 0, minor = 0, patch = 0;

            if (parts.Length >= 1 && int.TryParse(parts[0], out var m))
                major = m;

            if (parts.Length >= 2 && int.TryParse(parts[1], out var n))
                minor = n;

            if (parts.Length >= 3 && int.TryParse(parts[2], out var p))
                patch = p;

            return new SemVer
            {
                Major = major,
                Minor = minor,
                Patch = patch,
                Prerelease = prerelease
            };
        }

        /// <summary>
        /// Compares two version strings using SemVer semantics.
        /// Returns negative if v1 &lt; v2, zero if equal, positive if v1 &gt; v2.
        /// </summary>
        public static int CompareVersions(string? v1, string? v2)
        {
            var semVer1 = ParseVersion(v1);
            var semVer2 = ParseVersion(v2);

            if (semVer1 == null && semVer2 == null) return 0;
            if (semVer1 == null) return -1;
            if (semVer2 == null) return 1;

            return semVer1.CompareTo(semVer2);
        }

        /// <summary>
        /// Decides whether an incoming model should replace an existing model
        /// for the same namespace URI when re-importing into a workspace.
        ///
        /// Rules (in order):
        ///  1. Major version upgrade is NOT allowed — if majors differ,
        ///     keep the existing (returns false).
        ///  2. Within the same major, a strictly newer minor or patch wins
        ///     (returns true).
        ///  3. If versions tie (or neither parses), the side with the newer
        ///     publication date wins.
        ///  4. Otherwise the existing wins (idempotent re-import is a no-op).
        ///
        /// This is the canonical "should I overwrite the workspace's model"
        /// helper — use it from any importer (Cloud Library, file upload,
        /// dependency resolution) so the rule stays in one place.
        /// </summary>
        public static bool ShouldReplaceWithIncoming(
            string? incomingVersion, string? incomingPublicationDate,
            string? existingVersion, string? existingPublicationDate)
        {
            var iv = ParseVersion(incomingVersion);
            var ev = ParseVersion(existingVersion);

            // Rule 1: major-version upgrade is not allowed.
            if (iv != null && ev != null && iv.Major != ev.Major)
                return false;

            // Rule 2: strictly newer minor/patch wins.
            if (iv != null && ev != null)
            {
                int cmp = iv.CompareTo(ev);
                if (cmp > 0) return true;
                if (cmp < 0) return false;
                // versions equal — fall through to date tiebreaker
            }

            // Rule 3: publication date tiebreaker.
            var ipd = ParsePublicationDate(incomingPublicationDate);
            var epd = ParsePublicationDate(existingPublicationDate);
            if (ipd.HasValue && epd.HasValue) return ipd.Value > epd.Value;
            if (ipd.HasValue && !epd.HasValue) return true;

            // Rule 4: nothing decisive — keep existing.
            return false;
        }

        /// <summary>
        /// Parses a publication date string and returns just the date portion.
        /// </summary>
        public static DateOnly? ParsePublicationDate(string? dateString)
        {
            if (string.IsNullOrWhiteSpace(dateString))
                return null;

            if (DateTime.TryParse(dateString, out var dt))
                return DateOnly.FromDateTime(dt);

            return null;
        }

        /// <summary>
        /// Finds the best matching ModelInfo based on the specified criteria.
        /// </summary>
        public static IEnumerable<ModelInfo> FindMatches(
            IEnumerable<ModelInfo> models,
            string? modelUri,
            string? publicationDate,
            string? version)
        {
            var results = models.AsEnumerable();

            // Filter by ModelUri if specified
            if (!string.IsNullOrWhiteSpace(modelUri))
            {
                results = results.Where(m =>
                    m.ModelUri != null &&
                    m.ModelUri.Equals(modelUri, StringComparison.OrdinalIgnoreCase));
            }

            var filtered = results.ToList();

            // Apply publication date filter
            if (!string.IsNullOrWhiteSpace(publicationDate))
            {
                var targetDate = ParsePublicationDate(publicationDate);
                if (targetDate.HasValue)
                {
                    // First try exact match on date portion
                    var exactMatches = filtered.Where(m =>
                    {
                        var pubDate = ParsePublicationDate(m.PublicationDate);
                        return pubDate.HasValue && pubDate.Value == targetDate.Value;
                    }).ToList();

                    if (exactMatches.Count > 0)
                    {
                        filtered = exactMatches;
                    }
                    else
                    {
                        // No exact match - return latest greater than the PublicationDate
                        var laterDates = filtered.Where(m =>
                        {
                            var pubDate = ParsePublicationDate(m.PublicationDate);
                            return pubDate.HasValue && pubDate.Value > targetDate.Value;
                        }).ToList();

                        if (laterDates.Count > 0)
                        {
                            // Get the latest (closest to target date that's still greater)
                            var minLaterDate = laterDates
                                .Select(m => ParsePublicationDate(m.PublicationDate))
                                .Where(d => d.HasValue)
                                .Min();

                            filtered = laterDates.Where(m =>
                                ParsePublicationDate(m.PublicationDate) == minLaterDate).ToList();
                        }
                        else
                        {
                            filtered = new List<ModelInfo>();
                        }
                    }
                }
            }

            // Apply version filter
            if (!string.IsNullOrWhiteSpace(version))
            {
                var targetVersion = ParseVersion(version);
                if (targetVersion != null)
                {
                    filtered = FindBestVersionMatch(filtered, targetVersion);
                }
            }

            // If duplicates with same SemVer, return the one with newest publication date
            filtered = DeduplicateByVersion(filtered);

            return filtered;
        }

        /// <summary>
        /// Finds the best version match according to the rules:
        /// 1. Exact SemVer match
        /// 2. Latest revision for same major.minor
        /// 3. Latest with same major version
        /// 4. Nothing if no match on major
        /// </summary>
        private static List<ModelInfo> FindBestVersionMatch(List<ModelInfo> models, SemVer targetVersion)
        {
            // Try exact match first
            var exactMatches = models.Where(m =>
            {
                var ver = ParseVersion(m.ModelVersion);
                return ver != null && ver.CompareTo(targetVersion) == 0;
            }).ToList();

            if (exactMatches.Count > 0)
                return exactMatches;

            // Try same major.minor, get latest patch
            var sameMajorMinor = models.Where(m =>
            {
                var ver = ParseVersion(m.ModelVersion);
                return ver != null && ver.SameMajorMinor(targetVersion);
            }).ToList();

            if (sameMajorMinor.Count > 0)
            {
                var maxVersion = sameMajorMinor
                    .Select(m => ParseVersion(m.ModelVersion))
                    .Where(v => v != null)
                    .Max();

                return sameMajorMinor.Where(m =>
                {
                    var ver = ParseVersion(m.ModelVersion);
                    return ver != null && ver.CompareTo(maxVersion) == 0;
                }).ToList();
            }

            // Try same major version, get latest minor.patch
            var sameMajor = models.Where(m =>
            {
                var ver = ParseVersion(m.ModelVersion);
                return ver != null && ver.SameMajor(targetVersion);
            }).ToList();

            if (sameMajor.Count > 0)
            {
                var maxVersion = sameMajor
                    .Select(m => ParseVersion(m.ModelVersion))
                    .Where(v => v != null)
                    .Max();

                return sameMajor.Where(m =>
                {
                    var ver = ParseVersion(m.ModelVersion);
                    return ver != null && ver.CompareTo(maxVersion) == 0;
                }).ToList();
            }

            // No match on major version
            return new List<ModelInfo>();
        }

        /// <summary>
        /// Removes duplicates with same ModelUri and SemVer version, keeping the one with newest publication date.
        /// Different ModelUris with the same version are not considered duplicates.
        /// </summary>
        private static List<ModelInfo> DeduplicateByVersion(List<ModelInfo> models)
        {
            return models
                .GroupBy(m => new {
                    Uri = m.ModelUri?.ToLowerInvariant() ?? "",
                    Version = ParseVersion(m.ModelVersion)?.ToString() ?? ""
                })
                .Select(g => g
                    .OrderByDescending(m => ParsePublicationDate(m.PublicationDate))
                    .First())
                .ToList();
        }
    }
}
