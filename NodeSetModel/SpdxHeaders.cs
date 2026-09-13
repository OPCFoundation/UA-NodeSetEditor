using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NodeSetEditor.Model
{
    /// <summary>
    /// Reads and writes the (non-standard) SPDX comment headers used to carry license and
    /// copyright metadata in NodeSet XML, which has no standard element for either:
    /// <code>
    ///   &lt;!-- SPDX-FileCopyrightText: Copyright (C) 2026 OPC Foundation, Inc. --&gt;
    ///   &lt;!-- SPDX-License-Identifier: LicenseRef-OPC-Specification-1.15 --&gt;
    ///   &lt;!-- License: https://opcfoundation.org/license/specifications/1.15/ --&gt;
    /// </code>
    /// These headers are emitted on every XML export and parsed (best-effort) on import. The
    /// <c>License:</c> URL line is emitted only for a custom (<c>LicenseRef-…</c>) license — a
    /// standard SPDX id (MIT, Apache-2.0, …) is self-describing via the SPDX License List, so its
    /// URL is redundant and omitted (see <see cref="IsCustomLicenseId"/>).
    /// </summary>
    public static class SpdxHeaders
    {
        private static readonly Regex CommentRx =
            new(@"<!--(.*?)-->", RegexOptions.Singleline | RegexOptions.Compiled);
        // SPDX directives may be their own line-comments OR appear anywhere inside a larger header
        // comment block (the OPC Core NodeSet uses one big block comment).
        private static readonly Regex SpdxCopyrightRx =
            new(@"SPDX-FileCopyrightText:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SpdxLicenseIdRx =
            new(@"SPDX-License-Identifier:\s*([^\s<*]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SpdxLicenseUrlRx =
            new(@"\bLicense:\s*(https?://\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Fallbacks when the header carries no explicit SPDX directives.
        private static readonly Regex CopyrightLineRx =
            new(@"Copyright\b[^\r\n]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LicenseUrlAnywhereRx =
            new(@"https?://[^\s""'<>]*[Ll]icen[sc]e[^\s""'<>]*", RegexOptions.Compiled);
        private static readonly Regex CopyrightPrefixRx =
            new(@"^Copyright\s*(?:\(c\)|©)?\s*\d{1,4}(?:\s*[-–]\s*\d{1,4})?[,.\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AllRightsReservedRx =
            new(@"[\s,]*All\s+rights\s+reserved\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // The OPC Foundation reorganized as "OPC Federation AISBL"; normalize any legacy variation
        // ("OPC Foundation", "The OPC Foundation, Inc.", etc.) to the canonical legal name.
        private static readonly Regex OpcFoundationRx =
            new(@"OPC\s+Foundation", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Infer a well-known SPDX id from license prose when there is no explicit
        // SPDX-License-Identifier. Ordered most-specific first (AGPL/LGPL before GPL). MIT covers the
        // OPC Core NodeSet, whose header reads "OPC Foundation MIT License 1.00" + the MIT permission text.
        private static readonly (Regex Rx, string Spdx)[] LicenseInferences =
        {
            (new(@"\bMIT\s+License\b|Permission is hereby granted,\s+free of charge", RegexOptions.IgnoreCase | RegexOptions.Compiled), "MIT"),
            (new(@"Apache\s+License,?\s+(?:[Vv]ersion\s+)?2\.0|\bApache-2\.0\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Apache-2.0"),
            (new(@"\bBSD[\s-]*3[\s-]*Clause\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "BSD-3-Clause"),
            (new(@"\bBSD[\s-]*2[\s-]*Clause\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "BSD-2-Clause"),
            (new(@"Mozilla\s+Public\s+License,?\s+(?:[Vv]ersion\s+)?2\.0|\bMPL-2\.0\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "MPL-2.0"),
            (new(@"Affero\s+General\s+Public\s+License|\bAGPL-3\.0", RegexOptions.IgnoreCase | RegexOptions.Compiled), "AGPL-3.0-only"),
            (new(@"Lesser\s+General\s+Public\s+License|\bLGPL-3\.0", RegexOptions.IgnoreCase | RegexOptions.Compiled), "LGPL-3.0-only"),
            (new(@"General\s+Public\s+License,?\s+(?:[Vv]ersion\s+)?3|\bGPL-3\.0", RegexOptions.IgnoreCase | RegexOptions.Compiled), "GPL-3.0-only"),
            (new(@"General\s+Public\s+License,?\s+(?:[Vv]ersion\s+)?2|\bGPL-2\.0", RegexOptions.IgnoreCase | RegexOptions.Compiled), "GPL-2.0-only"),
            (new(@"\bISC\s+License\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ISC"),
        };

        /// <summary>Canonical copyright holder for the OPC Foundation, which is now "OPC Federation AISBL".</summary>
        public const string OpcFederationHolder = "OPC Federation AISBL";

        /// <summary>
        /// True when <paramref name="license"/> is a custom license identifier — an SPDX
        /// "LicenseRef-…" id (including the "DocumentRef-…:LicenseRef-…" form) rather than a standard
        /// SPDX License List id. Only custom licenses carry a reference URL in the emitted headers;
        /// standard ids (MIT, Apache-2.0, …) are self-describing so their URL is omitted.
        /// </summary>
        public static bool IsCustomLicenseId(string? license)
            => license?.Trim() is { Length: > 0 } id
               && id.Contains("LicenseRef-", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Parses license/copyright metadata from NodeSet XML header comments. Handles both the
        /// per-line SPDX directive format and a single large header comment (e.g. the OPC Core
        /// NodeSet): the license id comes from an explicit SPDX-License-Identifier or is inferred
        /// from the license text (MIT, Apache-2.0, …); the URL from an explicit "License:" header or
        /// any license URL in the comment; the copyright from SPDX-FileCopyrightText or a
        /// "Copyright …" line. Returns nulls for anything not found.
        /// </summary>
        public static (string? CopyrightHolder, string? License, string? LicenseUrl) Parse(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return (null, null, null);

            // Restrict the search to XML comment blocks (where the header lives), not node content.
            var sb = new StringBuilder();
            foreach (Match c in CommentRx.Matches(xml))
                sb.Append(c.Groups[1].Value).Append('\n');
            var comments = sb.ToString();
            if (comments.Trim().Length == 0) return (null, null, null);

            var license = First(SpdxLicenseIdRx, comments);
            if (license == null)
            {
                foreach (var (rx, spdx) in LicenseInferences)
                    if (rx.IsMatch(comments)) { license = spdx; break; }
            }

            var licenseUrl = First(SpdxLicenseUrlRx, comments)
                ?? First(LicenseUrlAnywhereRx, comments, group: 0);

            var copyrightRaw = First(SpdxCopyrightRx, comments)
                ?? First(CopyrightLineRx, comments, group: 0);

            return (ExtractCopyrightHolder(copyrightRaw), license, licenseUrl);
        }

        private static string? First(Regex rx, string text, int group = 1)
        {
            var m = rx.Match(text);
            return m.Success ? m.Groups[group].Value.Trim() : null;
        }

        /// <summary>
        /// Formats the SPDX-FileCopyrightText prose for a bare holder — "Copyright (C) {year}
        /// {holder}" — using <paramref name="year"/> (current UTC year when null). Returns null when
        /// there is no holder. Shared by the XML comment and JSON header emitters so both render the
        /// copyright identically.
        /// </summary>
        public static string? FormatCopyrightText(string? copyrightHolder, int? year = null)
            => string.IsNullOrWhiteSpace(copyrightHolder)
                ? null
                : $"Copyright (C) {year ?? DateTime.UtcNow.Year} {copyrightHolder.Trim()}";

        /// <summary>
        /// Parses license/copyright from a JSON NodeSet's top-level "SPDX" object (the JSON
        /// counterpart of the XML header comments). Returns nulls when the text is not a JSON object
        /// or carries no header. The copyright is reduced to the bare holder, matching <see cref="Parse"/>.
        /// </summary>
        private static (string? CopyrightHolder, string? License, string? LicenseUrl) ParseJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("SPDX", out var header)
                    || header.ValueKind != JsonValueKind.Object)
                    return (null, null, null);

                string? Get(string name) =>
                    header.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString()?.Trim() is { Length: > 0 } s ? s : null
                        : null;

                return (ExtractCopyrightHolder(Get("CopyrightText")), Get("LicenceId"), Get("LicenceRef"));
            }
            catch (JsonException)
            {
                return (null, null, null);
            }
        }

        /// <summary>
        /// Parses SPDX metadata from a NodeSet in either encoding: JSON (a top-level "SPDX"
        /// object) or XML (header comments). Dispatches on the first non-whitespace character.
        /// </summary>
        private static (string? CopyrightHolder, string? License, string? LicenseUrl) ParseAny(string? text)
        {
            if (string.IsNullOrEmpty(text)) return (null, null, null);

            var i = 0;
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '﻿')) i++;
            return i < text.Length && text[i] == '{' ? ParseJson(text) : Parse(text);
        }

        /// <summary>
        /// Recovers the bare copyright holder from a copyright string: strips a leading
        /// "Copyright (C) YYYY[-YYYY]" prefix and a trailing "All rights reserved.", then normalizes
        /// any "OPC Foundation" variation to the current legal entity. e.g.
        /// "Copyright (c) 2005-2026 The OPC Foundation, Inc. All rights reserved." → "OPC Federation AISBL".
        /// </summary>
        public static string? ExtractCopyrightHolder(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.Trim();
            var m = CopyrightPrefixRx.Match(trimmed);
            var holder = m.Success ? trimmed[m.Length..] : trimmed;
            holder = AllRightsReservedRx.Replace(holder, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(holder)) return null;
            // Always normalize any "OPC Foundation" variation to the current legal entity.
            if (OpcFoundationRx.IsMatch(holder)) holder = OpcFederationHolder;
            return holder;
        }

        /// <summary>
        /// Builds the SPDX comment block (one trailing newline per line) for the given values.
        /// Returns an empty string when there is nothing to emit. The copyright line is rendered
        /// as "Copyright (C) {year} {holder}" using <paramref name="year"/> (current UTC year when
        /// null). The License URL line is emitted only for a custom license (see
        /// <see cref="IsCustomLicenseId"/>); a standard SPDX id needs no URL.
        /// </summary>
        public static string BuildComment(string? copyrightHolder, string? license, string? licenseUrl, int? year = null)
        {
            var sb = new StringBuilder();
            var copyrightText = FormatCopyrightText(copyrightHolder, year);
            if (copyrightText != null)
                sb.Append($"<!-- SPDX-FileCopyrightText: {copyrightText} -->\n");
            if (!string.IsNullOrWhiteSpace(license))
                sb.Append($"<!-- SPDX-License-Identifier: {license.Trim()} -->\n");
            if (!string.IsNullOrWhiteSpace(licenseUrl) && IsCustomLicenseId(license))
                sb.Append($"<!-- License: {licenseUrl.Trim()} -->\n");
            return sb.ToString();
        }

        /// <summary>
        /// Injects the SPDX comment block into serialized NodeSet XML, immediately after the XML
        /// declaration (or at the very top when there is none). Returns the original bytes
        /// unchanged when there is nothing to emit. Preserves a UTF-8 BOM if present.
        /// </summary>
        public static byte[] InjectIntoXml(byte[] xml, string? copyrightHolder, string? license, string? licenseUrl, int? year = null)
        {
            var comment = BuildComment(copyrightHolder, license, licenseUrl, year);
            if (comment.Length == 0) return xml;

            var text = Encoding.UTF8.GetString(xml);
            var hasBom = text.Length > 0 && text[0] == '﻿';
            if (hasBom) text = text[1..];

            var insertAt = 0;
            if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                var end = text.IndexOf("?>", StringComparison.Ordinal);
                if (end >= 0) insertAt = end + 2;
            }

            var prefix = text[..insertAt];
            var rest = text[insertAt..];
            // Keep the newline that followed the declaration, then insert the comment block.
            if (rest.StartsWith("\r\n", StringComparison.Ordinal)) { prefix += "\r\n"; rest = rest[2..]; }
            else if (rest.StartsWith("\n", StringComparison.Ordinal)) { prefix += "\n"; rest = rest[1..]; }

            var bytes = Encoding.UTF8.GetBytes(prefix + comment + rest);
            if (!hasBom) return bytes;

            var bom = Encoding.UTF8.GetPreamble();
            var combined = new byte[bom.Length + bytes.Length];
            Buffer.BlockCopy(bom, 0, combined, 0, bom.Length);
            Buffer.BlockCopy(bytes, 0, combined, bom.Length, bytes.Length);
            return combined;
        }

        /// <summary>
        /// Stream convenience: reads <paramref name="xml"/> fully, injects the headers, and returns
        /// a new rewound MemoryStream.
        /// </summary>
        public static MemoryStream InjectIntoXml(Stream xml, string? copyrightHolder, string? license, string? licenseUrl, int? year = null)
        {
            using var buffer = new MemoryStream();
            xml.CopyTo(buffer);
            var injected = InjectIntoXml(buffer.ToArray(), copyrightHolder, license, licenseUrl, year);
            return new MemoryStream(injected) { Position = 0 };
        }

        // Defaults applied to OPC Foundation namespaces (http://opcfoundation.org/...) when the
        // source NodeSet carries no SPDX headers — e.g. the Core UA NodeSet the console imports at
        // DB reset. Adjust here if the Foundation's canonical SPDX id/URL changes.
        public const string OpcFoundationLicenseId = "LicenseRef-OPC-Foundation";
        public const string OpcFoundationLicenseUrl = "https://opcfoundation.org/license/";
        public const string OpcFoundationCopyright = OpcFederationHolder;

        /// <summary>
        /// Resolves the license/copyright to stamp onto a model the console imports from a file
        /// (Core UA NodeSet, dependencies, DbTool import-file). Handles both NodeSet encodings — XML
        /// header comments and the JSON top-level "SPDX" object. Precedence: embedded SPDX
        /// metadata, then — for an OPC Foundation namespace only — the Foundation defaults above.
        /// Returns nulls for a non-OPC file with no SPDX metadata (the caller then prompts / supplies
        /// values). The parameter is named <c>rawXml</c> for legacy reasons; JSON bytes are accepted too.
        /// </summary>
        public static (string? License, string? LicenseUrl, string? CopyrightHolder) ResolveForImport(byte[]? rawXml, string? modelUri)
        {
            string? text = null;
            if (rawXml is { Length: > 0 })
            {
                try { text = Encoding.UTF8.GetString(rawXml); } catch { /* not text — leave null */ }
            }
            var (copyright, license, url) = ParseAny(text);

            var isOpcFoundation = modelUri != null
                && modelUri.StartsWith("http://opcfoundation.org/", StringComparison.OrdinalIgnoreCase);
            if (isOpcFoundation)
            {
                if (string.IsNullOrWhiteSpace(license)) { license = OpcFoundationLicenseId; url ??= OpcFoundationLicenseUrl; }
                if (string.IsNullOrWhiteSpace(copyright)) copyright = OpcFoundationCopyright;
            }
            return (license, url, copyright);
        }
    }
}
