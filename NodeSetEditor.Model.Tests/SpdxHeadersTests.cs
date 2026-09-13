using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    public class SpdxHeadersTests
    {
        // The real OPC Core NodeSet header: a single block comment carrying the MIT license text,
        // a copyright with a year range, and the OPC MIT license URL — no literal SPDX directives.
        private const string OpcCoreHeader = """
            <?xml version="1.0" encoding="utf-8" ?>
            <!--
             * Copyright (c) 2005-2026 The OPC Foundation, Inc. All rights reserved.
             *
             * OPC Foundation MIT License 1.00
             *
             * Permission is hereby granted, free of charge, to any person
             * obtaining a copy of this software and associated documentation
             * files (the "Software"), to deal in the Software without
             * restriction ...
             *
             * The complete license agreement can be found here:
             * http://opcfoundation.org/License/MIT/1.00/
            -->
            <UANodeSet><Models><Model ModelUri="http://opcfoundation.org/UA/" /></Models></UANodeSet>
            """;

        [Fact]
        public void Parse_OpcCoreBlockComment_DetectsMit_Url_AndNormalizedHolder()
        {
            var (copyright, license, url) = SpdxHeaders.Parse(OpcCoreHeader);

            Assert.Equal("MIT", license);
            Assert.Equal("http://opcfoundation.org/License/MIT/1.00/", url);
            // "The OPC Foundation, Inc." (with a 2005-2026 range + "All rights reserved.") → canonical entity.
            Assert.Equal(SpdxHeaders.OpcFederationHolder, copyright);
        }

        [Fact]
        public void Parse_PerLineSpdxDirectives_AreHonoured()
        {
            const string xml = """
                <?xml version="1.0" encoding="utf-8" ?>
                <!-- SPDX-FileCopyrightText: Copyright (C) 2026 Acme Corp -->
                <!-- SPDX-License-Identifier: LicenseRef-OPC-Specification-1.15 -->
                <!-- License: https://opcfoundation.org/license/specifications/1.15/ -->
                <UANodeSet />
                """;

            var (copyright, license, url) = SpdxHeaders.Parse(xml);

            Assert.Equal("LicenseRef-OPC-Specification-1.15", license);
            Assert.Equal("https://opcfoundation.org/license/specifications/1.15/", url);
            Assert.Equal("Acme Corp", copyright);
        }

        [Fact]
        public void ExtractCopyrightHolder_NormalizesOpcFoundationVariations()
        {
            Assert.Equal(SpdxHeaders.OpcFederationHolder,
                SpdxHeaders.ExtractCopyrightHolder("Copyright (c) 2005-2026 The OPC Foundation, Inc. All rights reserved."));
            Assert.Equal(SpdxHeaders.OpcFederationHolder,
                SpdxHeaders.ExtractCopyrightHolder("OPC Foundation"));
            Assert.Equal("Acme, Inc.",
                SpdxHeaders.ExtractCopyrightHolder("Copyright 2026 Acme, Inc."));
        }

        [Fact]
        public void BuildComment_StandardLicense_OmitsUrl()
        {
            // A standard SPDX id is self-describing — the License URL line is dropped even when a URL
            // is supplied (e.g. an imported OPC nodeset carrying the OPC MIT license link).
            var comment = SpdxHeaders.BuildComment(
                SpdxHeaders.OpcFederationHolder, "MIT", "http://opcfoundation.org/License/MIT/1.00/", 2026);

            Assert.Contains("SPDX-License-Identifier: MIT", comment);
            Assert.DoesNotContain("License:", comment);
            Assert.DoesNotContain("opcfoundation.org/License/MIT", comment);
        }

        [Fact]
        public void BuildComment_CustomLicense_EmitsUrl()
        {
            var comment = SpdxHeaders.BuildComment(
                "Acme Corp", "LicenseRef-OPC-Specification-1.15",
                "https://opcfoundation.org/license/specifications/1.15/", 2026);

            Assert.Contains("SPDX-License-Identifier: LicenseRef-OPC-Specification-1.15", comment);
            Assert.Contains("<!-- License: https://opcfoundation.org/license/specifications/1.15/ -->", comment);
        }

        [Theory]
        [InlineData("LicenseRef-Custom", true)]
        [InlineData("LicenseRef-OPC-Foundation", true)]
        [InlineData("MIT", false)]
        [InlineData("Apache-2.0", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsCustomLicenseId_ClassifiesLicenses(string? license, bool expected)
        {
            Assert.Equal(expected, SpdxHeaders.IsCustomLicenseId(license));
        }

        [Fact]
        public void ResolveForImport_OpcNamespaceWithNoHeader_AppliesFoundationDefaults()
        {
            var (license, url, copyright) = SpdxHeaders.ResolveForImport(
                System.Text.Encoding.UTF8.GetBytes("<UANodeSet />"),
                "http://opcfoundation.org/UA/DI/");

            Assert.Equal(SpdxHeaders.OpcFoundationLicenseId, license);
            Assert.Equal(SpdxHeaders.OpcFoundationLicenseUrl, url);
            Assert.Equal(SpdxHeaders.OpcFederationHolder, copyright);
        }

        [Fact]
        public void ResolveForImport_OpcCoreHeader_PrefersDetectedMitOverDefaults()
        {
            var (license, url, copyright) = SpdxHeaders.ResolveForImport(
                System.Text.Encoding.UTF8.GetBytes(OpcCoreHeader),
                "http://opcfoundation.org/UA/");

            Assert.Equal("MIT", license);
            Assert.Equal("http://opcfoundation.org/License/MIT/1.00/", url);
            Assert.Equal(SpdxHeaders.OpcFederationHolder, copyright);
        }

        [Fact]
        public void ResolveForImport_JsonSpdxHeader_IsParsed()
        {
            // JSON NodeSets carry SPDX in a top-level "SPDX" object (JSON has no comments).
            const string json = """
                {
                  "SPDX": {
                    "CopyrightText": "Copyright (C) 2026 The OPC Foundation, Inc. All rights reserved.",
                    "LicenceId": "MIT",
                    "LicenceRef": "http://opcfoundation.org/License/MIT/1.00/"
                  },
                  "Models": [ { "ModelUri": "http://example.org/mymodel/" } ]
                }
                """;

            var (license, url, copyright) = SpdxHeaders.ResolveForImport(
                System.Text.Encoding.UTF8.GetBytes(json), "http://example.org/mymodel/");

            Assert.Equal("MIT", license);
            Assert.Equal("http://opcfoundation.org/License/MIT/1.00/", url);
            // The copyright prose is reduced to the (normalized) bare holder, matching the XML path.
            Assert.Equal(SpdxHeaders.OpcFederationHolder, copyright);
        }

        [Fact]
        public void ResolveForImport_JsonWithoutHeader_NonOpc_ReturnsNulls()
        {
            const string json = """{ "Models": [ { "ModelUri": "http://example.org/mymodel/" } ] }""";

            var (license, url, copyright) = SpdxHeaders.ResolveForImport(
                System.Text.Encoding.UTF8.GetBytes(json), "http://example.org/mymodel/");

            Assert.Null(license);
            Assert.Null(url);
            Assert.Null(copyright);
        }

        [Fact]
        public void ResolveForImport_JsonWithoutHeader_OpcNamespace_AppliesFoundationDefaults()
        {
            const string json = """{ "Models": [ { "ModelUri": "http://opcfoundation.org/UA/DI/" } ] }""";

            var (license, url, copyright) = SpdxHeaders.ResolveForImport(
                System.Text.Encoding.UTF8.GetBytes(json), "http://opcfoundation.org/UA/DI/");

            Assert.Equal(SpdxHeaders.OpcFoundationLicenseId, license);
            Assert.Equal(SpdxHeaders.OpcFoundationLicenseUrl, url);
            Assert.Equal(SpdxHeaders.OpcFederationHolder, copyright);
        }
    }
}
