using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp.Gate.Tests;

/// <summary>
/// The guard of the server's fix for security finding U-1 (<c>LeftOpenConnections</c>), which reads internals of
/// Microsoft.Data.SqlClient. It runs without SQL Server so that ci.yml and release.yml both run it. A SqlClient update
/// fails here on purpose: the internals must then be read again, for native (Windows) and managed networking, before
/// the version below is changed.
/// </summary>
public sealed class LeftOpenConnectionsGuardTests
{
    [Fact]
    public void The_referenced_SqlClient_is_the_version_whose_internals_were_read()
    {
        var assembly = typeof(SqlConnection).Assembly;
        var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

        Assert.Equal(LeftOpenConnections.ReadFromVersion, $"{fileVersion.FileMajorPart}.{fileVersion.FileMinorPart}.{fileVersion.FileBuildPart}");
        Assert.StartsWith(LeftOpenConnections.ReadFromVersion, informational, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_SqlClient_internal_of_the_fix_is_found()
    {
        Assert.True(LeftOpenConnections.Available, "LeftOpenConnections found not all SqlClient internals it needs; the U-1 fix does nothing.");
    }

    [Fact]
    public void The_network_handle_resolves_on_the_native_and_the_managed_state_object()
    {
        // Looked up by type when the class loads: both types exist in the Windows and the Unix build of SqlClient
        // 7.1.1, whichever this test host loads.
        var names = LeftOpenConnections.StateObjectTypes.Select(t => t.FullName ?? t.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
            new[] { "Microsoft.Data.SqlClient.ManagedSni.TdsParserStateObjectManaged", "Microsoft.Data.SqlClient.TdsParserStateObjectNative" },
            names);
    }
}
