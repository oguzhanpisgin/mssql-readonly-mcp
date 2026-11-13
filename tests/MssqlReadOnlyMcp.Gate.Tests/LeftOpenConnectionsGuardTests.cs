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

    // Each internal LeftOpenConnections needs, by name: when one is missing, Available is false (the test above fails)
    // and the case here names which one.
    [Theory]
    [InlineData("SqlConnection.Parser")]
    [InlineData("TdsParser._state, with the values Closed and Broken")]
    [InlineData("TdsParser._physicalStateObj")]
    [InlineData("TdsParserStateObject._pendingCallbacks (int)")]
    [InlineData("TdsParserStateObject.Dispose()")]
    [InlineData("TdsParser._connHandler")]
    [InlineData("internal connection IsConnectionDoomed (bool)")]
    [InlineData("internal connection Owner (DbConnection)")]
    public void Each_SqlClient_internal_of_the_fix_resolves(string member)
    {
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var parser = typeof(SqlConnection).GetProperty("Parser", Members);
        var parserType = parser?.PropertyType;
        var state = parserType?.GetField("_state", Members);
        var physical = parserType?.GetField("_physicalStateObj", Members);
        var handler = parserType?.GetField("_connHandler", Members);
        var resolved = member switch
        {
            "SqlConnection.Parser" => parser is not null,
            "TdsParser._state, with the values Closed and Broken" =>
                state is { FieldType.IsEnum: true } && Enum.IsDefined(state.FieldType, "Closed") && Enum.IsDefined(state.FieldType, "Broken"),
            "TdsParser._physicalStateObj" => physical is not null,
            "TdsParserStateObject._pendingCallbacks (int)" => physical?.FieldType.GetField("_pendingCallbacks", Members)?.FieldType == typeof(int),
            "TdsParserStateObject.Dispose()" => physical?.FieldType.GetMethod("Dispose", Members, Type.EmptyTypes) is not null,
            "TdsParser._connHandler" => handler is not null,
            "internal connection IsConnectionDoomed (bool)" => handler?.FieldType.GetProperty("IsConnectionDoomed", Members)?.PropertyType == typeof(bool),
            "internal connection Owner (DbConnection)" => handler?.FieldType.GetProperty("Owner", Members)?.PropertyType == typeof(System.Data.Common.DbConnection),
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, null),
        };

        Assert.True(resolved, $"SqlClient internal not found: {member}");
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
