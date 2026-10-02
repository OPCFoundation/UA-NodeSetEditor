using NodeSetEditor.Server.Controllers;
using Xunit;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// A dependency exported with "remove unused nodes" keeps only what one workspace's model
/// reaches, so its filename is prefixed with that workspace to mark it as not interchangeable
/// with the published NodeSet. Pure function — no database or server fixture required.
/// </summary>
public class WorkspaceFileNamePrefixTests
{
    [Theory]
    [InlineData("Dosing Review", "dosingreview_")]      // spaces dropped
    [InlineData("dosing", "dosing_")]
    [InlineData("DOSING", "dosing_")]                   // lowercased
    [InlineData("Randy's Workspace!", "randysworkspace_")] // punctuation dropped
    [InlineData("PlasticsRubber-2026_Q4", "plasticsrubber2026q4_")] // digits kept, separators not
    [InlineData("  padded  ", "padded_")]
    public void Slugifies(string workspaceName, string expected)
    {
        Assert.Equal(expected, UaRestApiController.WorkspaceFileNamePrefix(workspaceName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-_.")]     // nothing survives stripping
    public void IsEmptyWhenThereIsNoUsableName(string? workspaceName)
    {
        // Empty rather than "_": the caller concatenates this onto the generated name, and a
        // bare leading separator would be worse than no prefix at all.
        Assert.Equal(string.Empty, UaRestApiController.WorkspaceFileNamePrefix(workspaceName));
    }

    [Fact]
    public void ProducesTheFullTrimmedDependencyName()
    {
        // What the ZIP entry actually looks like, against the unprefixed name the same
        // dependency gets when it is NOT trimmed.
        var prefix = UaRestApiController.WorkspaceFileNamePrefix("Dosing Review");

        Assert.Equal(
            "dosingreview_opcua_di_1.05.0_2025_11_15.xml",
            prefix + "opcua_di_1.05.0_2025_11_15.xml");
    }
}
