using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MssqlReadOnlyMcp;

/// <summary>
/// Set by <see cref="CallToolAuditFilter"/> for one <c>tools/call</c> request; <see cref="ToolService"/> marks it when
/// the call reaches a tool, which then writes its own audit line.
/// </summary>
internal sealed class CallAuditMarker
{
    private static readonly AsyncLocal<CallAuditMarker?> CurrentMarker = new();

    private int audited;

    /// <summary>The marker of the call running on this async flow, or null outside the filter.</summary>
    public static CallAuditMarker? Current
    {
        get => CurrentMarker.Value;
        set => CurrentMarker.Value = value;
    }

    /// <summary>Whether a tool took the call (and so wrote or tried to write its own audit line).</summary>
    public bool Audited => Volatile.Read(ref audited) == 1;

    public void MarkAudited() => Interlocked.Exchange(ref audited, 1);
}

/// <summary>
/// The call-tool filter: every <c>tools/call</c> request runs inside it. A request the SDK rejects before a tool runs
/// (an unknown tool, a missing or mistyped argument) gets its own audit line here, with reason <c>InvalidRequest</c>
/// and no argument recorded. The call is also counted as in flight, so shutdown waits for it before the audit log closes.
/// </summary>
internal static class CallToolAuditFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var service = request.Services!.GetRequiredService<ToolService>();
            using var tracked = service.TrackCall();
            var marker = new CallAuditMarker();
            CallAuditMarker.Current = marker;
            var started = Stopwatch.GetTimestamp();
            CallToolResult result;
            try
            {
                result = await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!marker.Audited)
            {
                var written = service.AuditRejectedCall(request.Params?.Name, ElapsedMs(started));
                service.LogRejectedCall(request.Params?.Name, exception);
                if (exception is McpProtocolException or OperationCanceledException)
                {
                    // An unknown tool name stays the SDK's JSON-RPC error; a cancelled request stays cancelled.
                    throw;
                }

                // A missing or mistyped argument: the SDK would answer "An error occurred invoking '<tool>'.", which
                // carries no reason. The client gets the audit line's reason word first, like every other failure.
                return ErrorResult(written ? ToolErrors.InvalidRequestMessage : ToolErrors.AuditUnavailableMessage);
            }
            finally
            {
                CallAuditMarker.Current = null;
            }

            if (marker.Audited)
            {
                return result;
            }

            // No tool ran. Should the SDK ever answer such a call with data, it is withheld when its line cannot be
            // written, as for any tool call.
            var lineWritten = service.AuditRejectedCall(request.Params?.Name, ElapsedMs(started));
            if (result.IsError == true)
            {
                return ErrorResult(lineWritten ? ToolErrors.InvalidRequestMessage : ToolErrors.AuditUnavailableMessage);
            }

            return lineWritten ? result : ErrorResult(ToolErrors.AuditUnavailableMessage);
        };

    private static CallToolResult ErrorResult(string text) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
