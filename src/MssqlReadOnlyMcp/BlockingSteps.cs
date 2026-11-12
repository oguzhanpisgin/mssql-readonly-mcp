using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp;

/// <summary>
/// Runs these synchronous SqlClient steps of a call on a thread of their own: the transaction start, the command cancel
/// with the reader close, the plain reader close (both drain to the end of the result or to the cancel
/// acknowledgement), the rollback, the connection close (run_query, both schema tools, and a failed session open), and
/// the <c>xml</c> reads of a result. A stalled or frozen connection in one of these steps then holds that thread, not
/// one of the thread pool's, so the timers and continuations that answer the other calls at their limit keep running.
/// Not moved: the other synchronous value reads of <c>run_query</c> (<c>GetValue</c>, <c>GetSqlDecimal</c>, which under
/// sequential access can wait for the rest of a value on the network) and the cancel SqlClient runs inline when a token
/// is cancelled; a call frozen in the middle of a row holds a thread-pool thread there. Each held step holds one OS
/// thread; nothing limits their number beyond the connections that can be held. The threads are background threads:
/// they never keep the process alive.
/// </summary>
internal static class BlockingSteps
{
    private const TaskCreationOptions OwnThread = TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach;

    public static Task RunAsync(Action step) =>
        Task.Factory.StartNew(step, CancellationToken.None, OwnThread, TaskScheduler.Default);

    public static Task<T> RunAsync<T>(Func<T> step) =>
        Task.Factory.StartNew(step, CancellationToken.None, OwnThread, TaskScheduler.Default);

    /// <summary>
    /// Closes the reader, the command and the connection of a call, on a thread of their own. When the call's token was
    /// cancelled (its deadline or the client's cancel reached it), the physical connection is discarded rather than
    /// returned to the pool: <see cref="SqlConnection.ClearPool"/> marks every connection of the pool, also those other
    /// calls are using, to be closed rather than pooled when it is released.
    /// </summary>
    public static Task CloseAsync(
        SqlConnection connection, SqlCommand? command, SqlDataReader? reader, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            try
            {
                reader?.Dispose();
            }
            finally
            {
                command?.Dispose();
                DiscardIfCancelled(connection, cancellationToken);
                connection.Dispose();
            }
        });

    /// <summary>
    /// Marks the physical connection to be discarded when it closes, if the call's token was cancelled and the
    /// connection was opened. Called on the closing thread, right before the close.
    /// </summary>
    public static void DiscardIfCancelled(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested && connection.State != ConnectionState.Closed)
        {
            SqlConnection.ClearPool(connection);
        }
    }
}

/// <summary>
/// One thread of its own for a sequence of synchronous steps (the <c>xml</c> reads of one result), so a result with
/// many such values creates one thread rather than one per value. The thread starts at the first step and ends when
/// this is disposed and the step it is running, if any, has ended. Steps run one at a time, in the order given; the
/// caller awaits each before giving the next.
/// </summary>
internal sealed class StepThread : IDisposable
{
    private readonly BlockingCollection<Action> steps = new();
    private bool started;

    public Task<T> RunAsync<T>(Func<T> step)
    {
        // Continuations run on the thread pool, never on this thread: the caller's flow does not move onto it.
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        steps.Add(() =>
        {
            try
            {
                done.SetResult(step());
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        });

        if (!started)
        {
            started = true;
            _ = BlockingSteps.RunAsync(RunSteps);
        }

        return done.Task;
    }

    public void Dispose()
    {
        steps.CompleteAdding();
        if (!started)
        {
            steps.Dispose();
        }
    }

    private void RunSteps()
    {
        foreach (var step in steps.GetConsumingEnumerable())
        {
            step();
        }

        steps.Dispose();
    }
}

/// <summary>
/// The connection, command and reader of one schema call, closed together by <see cref="BlockingSteps.CloseAsync"/>
/// when the scope ends (reader, then command, then connection, as nested <c>await using</c> declarations would).
/// </summary>
internal sealed class SessionScope(SqlConnection connection, CancellationToken cancellationToken) : IAsyncDisposable
{
    public SqlConnection Connection => connection;

    public SqlCommand? Command { get; set; }

    public SqlDataReader? Reader { get; set; }

    public ValueTask DisposeAsync() => new(BlockingSteps.CloseAsync(connection, Command, Reader, cancellationToken));
}
