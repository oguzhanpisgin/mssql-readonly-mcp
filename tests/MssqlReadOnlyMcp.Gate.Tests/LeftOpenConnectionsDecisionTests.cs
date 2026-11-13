using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp.Gate.Tests;

/// <summary>
/// Which connections <c>LeftOpenConnections</c> closes (security finding U-1), decided on SqlClient 7.1.1's own objects
/// in the states a break leaves, without SQL Server or a network: a TDS parser, its physical state object (native and
/// managed networking) and its internal connection are created without their constructors and given the field values
/// the diagnosis recorded. The parser is registered for the connection as <c>Track</c> does after an open. The
/// network handle is a stand-in object: SqlClient's <c>Dispose</c> clears the handle field and then disposes the
/// handle, which fails on the stand-in; <c>Close</c> ignores that failure by design, and the cleared field shows that
/// SqlClient's close of the state object ran.
/// </summary>
public sealed class LeftOpenConnectionsDecisionTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static TheoryData<string> StateObjectTypes() => new()
    {
        "Microsoft.Data.SqlClient.TdsParserStateObjectNative",
        "Microsoft.Data.SqlClient.ManagedSni.TdsParserStateObjectManaged",
    };

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_broken_parser_of_a_doomed_closed_connection_with_its_handle_and_no_pending_callback_is_found_and_closed(string stateObjectType)
    {
        // What a timeout in the middle of a row leaves: the parser broken a second time after Disconnect, the
        // connection doomed and closed by SqlClient, the socket's handle still held, no read pending.
        var left = Broken(stateObjectType);

        var found = LeftOpenConnections.FindLeftOpen(left.Connection);
        Assert.Same(left.StateObject, found);

        LeftOpenConnections.Close(found);
        Assert.False(left.HoldsHandle, "the state object still holds its network handle after Close");
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_broken_parser_whose_state_object_has_a_pending_callback_is_not_closed(string stateObjectType)
    {
        var left = Broken(stateObjectType, pendingCallbacks: 1);

        Assert.Null(LeftOpenConnections.FindLeftOpen(left.Connection));

        // Close checks again: handed the object anyway, it leaves it as it is.
        LeftOpenConnections.Close(left.StateObject);
        Assert.True(left.HoldsHandle);
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_broken_parser_whose_state_object_has_no_handle_is_not_returned(string stateObjectType)
    {
        Assert.Null(LeftOpenConnections.FindLeftOpen(Broken(stateObjectType, handle: false).Connection));
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_broken_parser_of_a_connection_that_is_not_doomed_is_not_returned(string stateObjectType)
    {
        var left = Broken(stateObjectType, doomed: false);

        Assert.Null(LeftOpenConnections.FindLeftOpen(left.Connection));
        Assert.True(left.HoldsHandle);
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_broken_parser_of_a_connection_that_is_still_open_is_not_returned(string stateObjectType)
    {
        var left = Broken(stateObjectType, connectionOpen: true);
        Assert.Equal(ConnectionState.Open, left.Connection.State);

        Assert.Null(LeftOpenConnections.FindLeftOpen(left.Connection));
        Assert.True(left.HoldsHandle);
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_closed_parser_with_its_handle_and_no_pending_callback_is_still_found_and_closed(string stateObjectType)
    {
        // The case the first fold handled (the break ended in Disconnect); its connection is not doomed-checked.
        var left = Build(stateObjectType, "Closed", pendingCallbacks: 0, handle: true, doomed: false, connectionOpen: false);

        var found = LeftOpenConnections.FindLeftOpen(left.Connection);
        Assert.Same(left.StateObject, found);

        LeftOpenConnections.Close(found);
        Assert.False(left.HoldsHandle);
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_parser_that_is_logged_in_is_not_returned(string stateObjectType)
    {
        var left = Build(stateObjectType, "OpenLoggedIn", pendingCallbacks: 0, handle: true, doomed: true, connectionOpen: false);

        Assert.Null(LeftOpenConnections.FindLeftOpen(left.Connection));
        Assert.True(left.HoldsHandle);
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_broken_parser_whose_internal_connection_another_connection_owns_is_not_returned(string stateObjectType)
    {
        // The pool handed the internal connection to another call (PostPop set that call's connection as its owner).
        var left = Broken(stateObjectType, owner: Owner.Other);

        Assert.Null(LeftOpenConnections.FindLeftOpen(left.Connection));
        Assert.True(left.HoldsHandle);
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_broken_parser_whose_internal_connection_this_connection_still_owns_is_found_and_closed(string stateObjectType)
    {
        var left = Broken(stateObjectType, owner: Owner.This);

        var found = LeftOpenConnections.FindLeftOpen(left.Connection);
        Assert.Same(left.StateObject, found);

        LeftOpenConnections.Close(found);
        Assert.False(left.HoldsHandle);
    }

    [Theory]
    [MemberData(nameof(StateObjectTypes))]
    public void A_broken_parser_without_an_internal_connection_is_not_returned(string stateObjectType)
    {
        // Doomed unknown: there is no internal connection to read it from.
        var left = Broken(stateObjectType, withInternalConnection: false);

        Assert.Null(LeftOpenConnections.FindLeftOpen(left.Connection));
        Assert.True(left.HoldsHandle);
    }

    [Fact]
    public void A_broken_parser_whose_state_object_is_of_an_unknown_type_is_not_returned()
    {
        // No SqlClient 7.1.1 state object type is unknown to the fix (it resolves the handle field of every concrete
        // one), so a stand-in of another type is stored in the parser, past the field's declared type.
        var left = Broken("Microsoft.Data.SqlClient.TdsParserStateObjectNative", unknownStateObject: true);
        Assert.IsType<UnknownStateObject>(left.StateObject);

        Assert.Null(LeftOpenConnections.FindLeftOpen(left.Connection));
        LeftOpenConnections.Close(left.StateObject);
    }

    private enum Owner
    {
        None,
        This,
        Other,
    }

    private static Left Broken(
        string stateObjectType,
        int pendingCallbacks = 0,
        bool handle = true,
        bool doomed = true,
        bool connectionOpen = false,
        Owner owner = Owner.None,
        bool withInternalConnection = true,
        bool unknownStateObject = false) =>
        Build(stateObjectType, "Broken", pendingCallbacks, handle, doomed, connectionOpen, owner, withInternalConnection, unknownStateObject);

    private static Left Build(
        string stateObjectType,
        string parserState,
        int pendingCallbacks,
        bool handle,
        bool doomed,
        bool connectionOpen,
        Owner owner = Owner.None,
        bool withInternalConnection = true,
        bool unknownStateObject = false)
    {
        Assert.True(LeftOpenConnections.Available);
        var assembly = typeof(SqlConnection).Assembly;
        var parserType = typeof(SqlConnection).GetProperty("Parser", Members)!.PropertyType;

        var stateObject = Uninitialized(assembly.GetType(stateObjectType, throwOnError: true)!);
        var handleField = Field(stateObject.GetType(), "_sessionHandle");
        if (handle)
        {
            var handleType = handleField.FieldType.IsAbstract
                ? assembly.GetTypes().Where(t => !t.IsAbstract && handleField.FieldType.IsAssignableFrom(t)).OrderBy(t => t.FullName, StringComparer.Ordinal).First()
                : handleField.FieldType;
            handleField.SetValue(stateObject, Uninitialized(handleType));
        }

        Field(stateObject.GetType(), "_pendingCallbacks").SetValue(stateObject, pendingCallbacks);

        var connection = new SqlConnection();
        GC.SuppressFinalize(connection);
        var otherConnection = new SqlConnection();
        GC.SuppressFinalize(otherConnection);

        // The internal connection: doomed or not, and its owner as the pool leaves it (none after PrePush, a
        // connection after PostPop). An uninitialized one has no weak reference at all, so one is always set.
        var connectionHandler = Field(parserType, "_connHandler");
        var internalConnection = Uninitialized(connectionHandler.FieldType);
        Field(internalConnection.GetType(), "<IsConnectionDoomed>k__BackingField").SetValue(internalConnection, doomed);
        var ownerConnection = owner switch
        {
            Owner.This => connection,
            Owner.Other => otherConnection,
            _ => null,
        };
        Field(internalConnection.GetType(), "_owningObject").SetValue(internalConnection, new WeakReference<DbConnection>(ownerConnection!));

        var parser = Uninitialized(parserType);
        var stateField = Field(parserType, "_state");
        stateField.SetValue(parser, Enum.Parse(stateField.FieldType, parserState));
        var physicalField = Field(parserType, "_physicalStateObj");
        object storedStateObject = stateObject;
        if (unknownStateObject)
        {
            storedStateObject = new UnknownStateObject();
            StorePastDeclaredType(physicalField, parser, storedStateObject);
        }
        else
        {
            physicalField.SetValue(parser, stateObject);
        }

        if (withInternalConnection)
        {
            connectionHandler.SetValue(parser, internalConnection);
        }

        if (connectionOpen)
        {
            // The SqlConnection still holds the internal connection, open: SqlClient has not closed it.
            Field(internalConnection.GetType(), "<State>k__BackingField").SetValue(internalConnection, ConnectionState.Open);
            Field(typeof(SqlConnection), "_innerConnection").SetValue(connection, internalConnection);
        }

        // As Track does right after an open.
        var parsers = (ConditionalWeakTable<SqlConnection, object>)typeof(LeftOpenConnections)
            .GetField("Parsers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        parsers.AddOrUpdate(connection, parser);

        return new Left(connection, storedStateObject, handleField, otherConnection);
    }

    // FieldInfo.SetValue checks the field's declared type; a store from IL does not.
    private static void StorePastDeclaredType(FieldInfo field, object target, object value)
    {
        var method = new DynamicMethod("Store", null, [typeof(object), typeof(object)], typeof(LeftOpenConnectionsDecisionTests).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        method.CreateDelegate<Action<object, object>>()(target, value);
    }

    private static object Uninitialized(Type type)
    {
        // Never constructed, so never finalized: a finalizer would run on fields no constructor set.
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        GC.SuppressFinalize(instance);
        return instance;
    }

    private static FieldInfo Field(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetField(name, Members | BindingFlags.DeclaredOnly) is { } field)
            {
                return field;
            }
        }

        throw new InvalidOperationException($"SqlClient {LeftOpenConnections.ReadFromVersion}: no field {name} on {type.FullName}.");
    }

    // OtherConnection keeps the other owner alive: the internal connection holds its owner through a weak reference.
    private sealed record Left(SqlConnection Connection, object StateObject, FieldInfo HandleField, SqlConnection OtherConnection)
    {
        public bool HoldsHandle => HandleField.GetValue(StateObject) is not null;
    }

    private sealed class UnknownStateObject;
}
