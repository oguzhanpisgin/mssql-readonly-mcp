using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp;

/// <summary>
/// The only place that opens connections and creates commands. It counts both, so tests can show that a refused
/// call sent nothing to SQL Server.
/// </summary>
internal sealed class SqlGateway(string connectionString, int lockTimeoutMs)
{
    private int connectionsOpened;
    private int commandsCreated;
    private long bytesReceived;

    /// <summary>Connections opened through this gateway.</summary>
    public int ConnectionsOpened => Volatile.Read(ref connectionsOpened);

    /// <summary>Commands created (and so sent) through this gateway, session settings included.</summary>
    public int CommandsCreated => Volatile.Read(ref commandsCreated);

    /// <summary>TDS bytes received on the connections passed to <see cref="RecordReceived"/>.</summary>
    public long BytesReceived => Interlocked.Read(ref bytesReceived);

    /// <summary>
    /// Opens a new connection and runs the fixed session settings as their own command before anything else.
    /// </summary>
    public async Task<SqlConnection> OpenSessionAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        // Statistics on: the bytes a read transferred are measurable (how much of a long value crossed the network).
        var connection = new SqlConnection(connectionString) { StatisticsEnabled = true };
        try
        {
            Interlocked.Increment(ref connectionsOpened);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Checked here rather than left to SqlClient: a call past its deadline sends no further command.
            cancellationToken.ThrowIfCancellationRequested();

            // NOCOUNT OFF whatever the instance's 'user options' say: the rows-changed check reads the row counts.
            var settings = string.Create(
                CultureInfo.InvariantCulture,
                $"SET LOCK_TIMEOUT {lockTimeoutMs}; SET DEADLOCK_PRIORITY LOW; SET QUOTED_IDENTIFIER ON; SET NOCOUNT OFF;");
            await using var command = CreateCommand(connection, settings, null, timeoutSeconds);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await BlockingSteps.CloseAsync(connection, null, null, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A new gateway on the same connection string with <c>Initial Catalog</c> set to <paramref name="database"/>: the
    /// tools then run in the database the permission check ran in, even when the connection string names none and the
    /// login's default database changes later.
    /// </summary>
    public SqlGateway PinnedTo(string database) =>
        new(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString, lockTimeoutMs);

    /// <summary>Adds the bytes <paramref name="connection"/> has received so far to <see cref="BytesReceived"/>.</summary>
    public void RecordReceived(SqlConnection connection)
    {
        if (connection.RetrieveStatistics()["BytesReceived"] is long received)
        {
            Interlocked.Add(ref bytesReceived, received);
        }
    }

    /// <summary>Creates a text command on <paramref name="connection"/>.</summary>
    public SqlCommand CreateCommand(SqlConnection connection, string text, SqlTransaction? transaction, int timeoutSeconds)
    {
        Interlocked.Increment(ref commandsCreated);
        return new SqlCommand(text, connection, transaction)
        {
            CommandType = System.Data.CommandType.Text,
            CommandTimeout = timeoutSeconds,
        };
    }
}
