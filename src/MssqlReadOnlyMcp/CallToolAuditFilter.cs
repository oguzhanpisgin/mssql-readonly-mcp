using System.Diagnostics;
using System.Text.Json.Nodes;
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
/// The call-tool filter: every <c>tools/call</c> request the SDK hands to its call-tool filters runs inside it. A
/// request the SDK rejects before a tool runs (an unknown tool, a missing or mistyped argument) gets its own audit line
/// here, with reason <c>InvalidRequest</c> and no argument recorded. The call is also counted as in flight, so shutdown
/// waits for it before the audit log closes. A request whose parameters the SDK cannot deserialize never reaches this
/// filter; the message filter <see cref="WrapMessages"/> audits it instead.
/// </summary>
internal static class CallToolAuditFilter
{
    /// <summary>The message <c>Items</c> key the call-tool filter sets when a <c>tools/call</c> request reaches it.</summary>
    private const string ReachedKey = "mssql-readonly-mcp.call-tool-filter";

    /// <summary>
    /// The incoming-message filter, around the SDK's whole handling of a message. The SDK deserializes the parameters of
    /// a <c>tools/call</c> request before the call-tool filter runs; when that throws (a lone surrogate in any string of
    /// <c>params</c>, <c>arguments</c> that is not an object, no tool name), the call-tool filter never sees the call.
    /// Such a request gets its audit line here, before the exception reaches the SDK, which then answers with its
    /// generic JSON-RPC error ("An error occurred."), as before. The answer carries no data either way, also when the
    /// line cannot be written (the service logs that failure).
    /// <para>
    /// It sees only requests the SDK passes to user message filters. It does not see a line the stdio transport drops
    /// or rejects before it is a request (malformed JSON, a batch array, an <c>id</c> that is not a string or a 64-bit
    /// integer, a lone surrogate in <c>method</c> or <c>id</c>), a <c>tools/call</c> sent as a notification, nor a
    /// request the SDK's built-in per-request <c>_meta</c> check rejects on protocol revision 2026-07-28 (it runs before
    /// user message filters). No tool runs for any of these.
    /// </para>
    /// </summary>
    public static McpMessageHandler WrapMessages(McpMessageHandler next) =>
        async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is not JsonRpcRequest { Method: RequestMethods.ToolsCall } request)
            {
                await next(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            var service = (context.Services ?? context.Server.Services)!.GetRequiredService<ToolService>();
            using var tracked = service.TrackCall();
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!context.Items.ContainsKey(ReachedKey))
            {
                var name = ReadToolName(request.Params);
                service.AuditRejectedCall(name, ElapsedMs(started));
                service.LogRejectedCall(name, exception);
                throw;
            }
        };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var service = request.Services!.GetRequiredService<ToolService>();

            // Set only once this filter can write the line itself; before that, the message filter writes it.
            request.Items[ReachedKey] = true;
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

    /// <summary>The <c>name</c> parameter when it is a string that can be read as text, else null (audited as <c>unknown</c>).</summary>
    private static string? ReadToolName(JsonNode? parameters)
    {
        try
        {
            return parameters is JsonObject named && named.TryGetPropertyValue("name", out var name)
                && name is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        }
        catch (Exception)
        {
            // A lone surrogate in a property name or in the name itself, or a repeated property name: not readable.
            return null;
        }
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
