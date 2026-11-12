using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp;

/// <summary>
/// Closes the network connection that Microsoft.Data.SqlClient 7.1.1 leaves open when it breaks a connection under a
/// reader or a command (security finding U-1).
/// <para>
/// When a read times out after a cancel (an attention) was sent, or the cancel SqlClient sends itself on a timeout is
/// not acknowledged in time, SqlClient marks the connection broken and closes it (<c>BreakConnection</c>). The close
/// ends in <c>TdsParser.Disconnect</c>, which disposes the physical state object, the owner of the socket, only when no
/// reader or command owns it. A reader that is reading or draining always does, so the socket stays open, out of the
/// pool and no longer reachable from the <see cref="SqlConnection"/>: <see cref="SqlConnection.ClearPool"/> and
/// <see cref="SqlConnection.Dispose"/> find a closed connection. SQL Server keeps that session, with its open
/// transaction and its locks, until the garbage collector happens to finalize the socket's handle or the process ends.
/// </para>
/// <para>
/// Use: <see cref="Track"/> right after the open; <see cref="FindLeftOpen"/> right before the close, while the
/// connection still belongs to the call; <see cref="Close"/> after the close. <see cref="FindLeftOpen"/> returns the
/// physical state object only when the connection's TDS parser is <c>Closed</c> and the state object still holds its
/// network handle: exactly what the break leaves. A parser is <c>Closed</c> only by <c>TdsParser.Disconnect</c>, and a
/// parser is never connected again (SqlClient creates a new one per login), so a connection found in that state can
/// no longer be in the pool or used by another call; a healthy connection is never found, so the one
/// <see cref="SqlConnection.Dispose"/> returns to the pool is never touched. <see cref="Close"/> checks the handle
/// again and disposes the state object, which closes the socket. A state object with a network callback still pending
/// is left alone (the socket then stays open, as without this class): disposing it would free what the callback needs.
/// </para>
/// <para>
/// SqlClient has no public API for this: the members are internals of 7.1.1 (<see cref="ReadFromVersion"/>), looked
/// up once, the network handle field on every concrete state object type of the loaded SqlClient (native and managed
/// networking). When one is missing (another SqlClient version), <see cref="Available"/> is false and nothing is done
/// here; the unit test <c>LeftOpenConnectionsGuardTests</c> then fails, as it does for any other SqlClient version.
/// Best effort: no method of this class throws, and none writes anything.
/// </para>
/// </summary>
internal static class LeftOpenConnections
{
    /// <summary>The Microsoft.Data.SqlClient version whose internals this class was written against.</summary>
    internal const string ReadFromVersion = "7.1.1";

    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly ConditionalWeakTable<SqlConnection, object> Parsers = new();

    private static readonly Internals? Members = Internals.Find();

    /// <summary>True when every SqlClient internal this class needs was found.</summary>
    internal static bool Available => Members is not null;

    /// <summary>
    /// The concrete physical state object types of the loaded SqlClient whose network handle field was found; empty
    /// when <see cref="Available"/> is false.
    /// </summary>
    internal static IReadOnlyCollection<Type> StateObjectTypes => Members?.StateObjectTypes ?? [];

    /// <summary>Records the TDS parser of <paramref name="connection"/>; call right after it opened. Never throws.</summary>
    public static void Track(SqlConnection connection)
    {
        try
        {
            if (Members?.ParserOf(connection) is { } parser)
            {
                Parsers.AddOrUpdate(connection, parser);
            }
        }
        catch (Exception)
        {
            // Best effort: an untracked connection is closed as before, without this step.
        }
    }

    /// <summary>
    /// Call right before closing <paramref name="connection"/>: the physical state object SqlClient broke and left
    /// open, to pass to <see cref="Close"/> after the close; null for any other connection. Never throws.
    /// </summary>
    public static object? FindLeftOpen(SqlConnection connection)
    {
        try
        {
            if (Members is null || !Parsers.TryGetValue(connection, out var parser))
            {
                return null;
            }

            Parsers.Remove(connection);
            return Members.LeftOpenStateObject(parser);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// After the close: disposes <paramref name="stateObject"/> (from <see cref="FindLeftOpen"/>) if it still holds
    /// its network handle, which closes the socket. Nothing for null. Never throws.
    /// </summary>
    public static void Close(object? stateObject)
    {
        if (stateObject is null || Members is null)
        {
            return;
        }

        try
        {
            Members.Dispose(stateObject);
        }
        catch (Exception)
        {
            // Already closed by SqlClient meanwhile, or not closable: nothing more can be done from here.
        }
    }

    private sealed class Internals(
        PropertyInfo parser,
        FieldInfo state,
        FieldInfo physical,
        FieldInfo pendingCallbacks,
        MethodInfo dispose,
        object closed,
        Dictionary<Type, FieldInfo> handles)
    {
        public IReadOnlyCollection<Type> StateObjectTypes => handles.Keys;

        public static Internals? Find()
        {
            try
            {
                var parser = typeof(SqlConnection).GetProperty("Parser", Internal);
                var state = parser?.PropertyType.GetField("_state", Internal);
                var physical = parser?.PropertyType.GetField("_physicalStateObj", Internal);
                var dispose = physical?.FieldType.GetMethod("Dispose", Internal, Type.EmptyTypes);
                var pendingCallbacks = physical?.FieldType.GetField("_pendingCallbacks", Internal);
                if (parser is null || state is null || physical is null || dispose is null || !state.FieldType.IsEnum
                    || pendingCallbacks?.FieldType != typeof(int) || !Enum.TryParse(state.FieldType, "Closed", out var closed))
                {
                    return null;
                }

                // Every concrete physical state object type (native and managed networking) by type, not only the one
                // a live connection happens to use; all must have the handle field.
                var handles = new Dictionary<Type, FieldInfo>();
                foreach (var type in LoadableTypes(physical.FieldType.Assembly))
                {
                    if (type.IsAbstract || !physical.FieldType.IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (HandleField(type) is not { } handle)
                    {
                        return null;
                    }

                    handles[type] = handle;
                }

                return handles.Count == 0 ? null : new Internals(parser, state, physical, pendingCallbacks, dispose, closed!, handles);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public object? ParserOf(SqlConnection connection) => parser.GetValue(connection);

        /// <summary>The parser's physical state object if the parser is closed and the object still holds its handle.</summary>
        public object? LeftOpenStateObject(object tdsParser)
        {
            if (!Equals(state.GetValue(tdsParser), closed) || physical.GetValue(tdsParser) is not { } stateObject)
            {
                return null;
            }

            return Closable(stateObject) ? stateObject : null;
        }

        public void Dispose(object stateObject)
        {
            if (Closable(stateObject))
            {
                dispose.Invoke(stateObject, null);
            }
        }

        // The handle is null once SqlClient disposed the state object itself; an unknown type is never touched. With a
        // network callback still pending (a read the network layer has not completed), the object is left as it is:
        // disposing it would free what that callback still needs. The break SqlClient performs has already released
        // its own reference (the count is 0 then), so a pending callback means something else may still use it.
        private bool Closable(object stateObject) =>
            handles.TryGetValue(stateObject.GetType(), out var handle)
            && handle.GetValue(stateObject) is not null
            && pendingCallbacks.GetValue(stateObject) is int pending && pending <= 0;

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.OfType<Type>();
            }
        }

        private static FieldInfo? HandleField(Type? type)
        {
            for (; type is not null; type = type.BaseType)
            {
                if (type.GetField("_sessionHandle", Internal | BindingFlags.DeclaredOnly) is { } field)
                {
                    return field;
                }
            }

            return null;
        }
    }
}
