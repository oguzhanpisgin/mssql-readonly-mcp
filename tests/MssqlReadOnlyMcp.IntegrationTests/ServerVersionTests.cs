namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// The version the server reports as MCP serverInfo.version: the informational version without the "+commit" part.
/// No server is involved.
/// </summary>
public sealed class ServerVersionTests
{
    [Theory]
    [InlineData("0.1.0+abc123", "0.1.0")]
    [InlineData("0.0.0-dev+abc", "0.0.0-dev")]
    [InlineData("1.2.3", "1.2.3")]
    public void Informational_version_is_cut_at_the_plus_sign(string informationalVersion, string expected)
    {
        Assert.Equal(expected, ServerVersion.FromInformationalVersion(informationalVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+abc")]
    public void Missing_or_empty_version_reports_the_fallback(string? informationalVersion)
    {
        Assert.Equal(ServerVersion.Fallback, ServerVersion.FromInformationalVersion(informationalVersion));
        Assert.Equal("0.0.0-unknown", ServerVersion.Fallback);
    }
}
