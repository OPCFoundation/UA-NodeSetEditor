using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The display name seeded for an imported model, derived from its namespace URI.
/// Everything after "/UA/" with the path separators read as spaces, so a nested namespace
/// keeps the context that tells it apart from another spec's identically named leaf.
/// </summary>
public class NamespaceNamingTests
{
    [Theory]
    // The common shape: one segment under /UA/, with and without a trailing slash.
    [InlineData("http://opcfoundation.org/UA/PADIM/", "PADIM")]
    [InlineData("http://opcfoundation.org/UA/DI", "DI")]
    // Nested namespaces keep the whole tail — "Jobs" alone would collide with any other
    // spec's /Jobs/ leaf, which is the reason for the rule.
    [InlineData("http://opcfoundation.org/UA/Dictionary/IRDI", "Dictionary IRDI")]
    [InlineData("http://opcfoundation.org/UA/Machinery/Jobs/", "Machinery Jobs")]
    [InlineData("http://opcfoundation.org/UA/Machinery/Result/Simple/", "Machinery Result Simple")]
    // A doubled separator must not leave a double space.
    [InlineData("http://opcfoundation.org/UA/Machinery//Jobs/", "Machinery Jobs")]
    // Core UA itself has nothing after the marker, so it falls back to the last segment
    // rather than deriving an empty name.
    [InlineData("http://opcfoundation.org/UA/", "UA")]
    // No /UA/ marker at all (a vendor's own namespace): last segment, as before.
    [InlineData("http://mycompany.example/MyPlant/Line1/", "Line1")]
    [InlineData("urn:example:MyModel", "MyModel")]
    // The marker is matched exactly, so a lowercase path is not treated as the UA marker.
    [InlineData("http://mycompany.example/ua/Thing/", "Thing")]
    public void DeriveFromUri_PrefersTheTailAfterTheUaSegment(string uri, string expected)
        => Assert.Equal(expected, NamespaceNaming.DeriveFromUri(uri));
}
