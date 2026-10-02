using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// The ProfileGroup:&lt;Name&gt; conformance-unit convention. These cover the encoding itself;
    /// that it is applied to the NamespaceMetadata object and to no other node is covered by the
    /// converter's round-trip tests.
    /// </summary>
    public class ProfileGroupConventionTests
    {
        // ---------- Split (import) ----------

        [Fact]
        public void Split_LiftsTheProfileGroupOutOfTheUnitList()
        {
            var (profileGroup, units) = ProfileGroupConvention.Split(
                ["Base Info", "ProfileGroup:UACore 1.05", "Base Info Server"]);

            Assert.Equal("UACore 1.05", profileGroup);
            Assert.Equal(new[] { "Base Info", "Base Info Server" }, units);
        }

        [Fact]
        public void Split_ReturnsNullUnitsWhenTheConventionWasTheOnlyEntry()
        {
            // "No units" is absence in the NodeSet XML, not an empty element list.
            var (profileGroup, units) = ProfileGroupConvention.Split(["ProfileGroup:UACore 1.05"]);

            Assert.Equal("UACore 1.05", profileGroup);
            Assert.Null(units);
        }

        [Fact]
        public void Split_LeavesAnOrdinaryUnitListAlone()
        {
            var (profileGroup, units) = ProfileGroupConvention.Split(["Base Info", "Base Info Server"]);

            Assert.Null(profileGroup);
            Assert.Equal(new[] { "Base Info", "Base Info Server" }, units);
        }

        [Theory]
        [InlineData("profilegroup:UACore 1.05")]
        [InlineData("PROFILEGROUP:UACore 1.05")]
        [InlineData("  ProfileGroup:UACore 1.05")]
        [InlineData("ProfileGroup:  UACore 1.05  ")]
        public void Split_IsForgivingAboutCasingAndSurroundingSpace(string unit)
        {
            // A hand-written NodeSet won't always match the canonical spelling; Merge still
            // writes the canonical form on the way out.
            var (profileGroup, units) = ProfileGroupConvention.Split([unit]);

            Assert.Equal("UACore 1.05", profileGroup);
            Assert.Null(units);
        }

        [Fact]
        public void Split_DropsAPrefixedEntryWithNoName()
        {
            // The spelling is reserved on this node whether or not it names anything.
            var (profileGroup, units) = ProfileGroupConvention.Split(["ProfileGroup:", "Base Info"]);

            Assert.Null(profileGroup);
            Assert.Equal(new[] { "Base Info" }, units);
        }

        [Fact]
        public void Split_TakesTheFirstOfSeveralAndDropsTheRest()
        {
            var (profileGroup, units) = ProfileGroupConvention.Split(
                ["ProfileGroup:First", "Keep Me", "ProfileGroup:Second"]);

            Assert.Equal("First", profileGroup);
            Assert.Equal(new[] { "Keep Me" }, units);
        }

        [Fact]
        public void Split_HandlesNullAndEmpty()
        {
            Assert.Equal((null, null), ProfileGroupConvention.Split(null));

            var (profileGroup, units) = ProfileGroupConvention.Split([]);
            Assert.Null(profileGroup);
            Assert.Empty(units!);
        }

        // ---------- Merge (export) ----------

        [Fact]
        public void Merge_PutsTheProfileGroupFirst()
        {
            var units = ProfileGroupConvention.Merge(["Base Info"], "UACore 1.05");

            Assert.Equal(new[] { "ProfileGroup:UACore 1.05", "Base Info" }, units);
        }

        [Fact]
        public void Merge_WritesTheCanonicalSpellingOverANonCanonicalOne()
        {
            var units = ProfileGroupConvention.Merge(["profilegroup:Old", "Base Info"], "UACore 1.05");

            Assert.Equal(new[] { "ProfileGroup:UACore 1.05", "Base Info" }, units);
        }

        [Fact]
        public void Merge_DoesNotDuplicateOnRoundTrip()
        {
            var exported = ProfileGroupConvention.Merge(["Base Info"], "UACore 1.05");
            var (profileGroup, units) = ProfileGroupConvention.Split(exported);
            var reExported = ProfileGroupConvention.Merge(units, profileGroup);

            Assert.Equal(exported, reExported);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Merge_WithNoProfileGroupJustStripsTheConvention(string? profileGroupName)
        {
            var units = ProfileGroupConvention.Merge(["ProfileGroup:Stale", "Base Info"], profileGroupName);

            Assert.Equal(new[] { "Base Info" }, units);
        }

        [Fact]
        public void Merge_OnANodeWithNoUnitsProducesJustTheConvention()
        {
            Assert.Equal(new[] { "ProfileGroup:UACore 1.05" },
                ProfileGroupConvention.Merge(null, "UACore 1.05"));
        }

        [Fact]
        public void Merge_WithNothingToWriteLeavesNull()
        {
            Assert.Null(ProfileGroupConvention.Merge(null, null));
        }
    }
}
