using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MssqlReadOnlyMcp.Audit;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// R-6: the built server as a child process over stdio, through the SDK's stdio client.
/// R-8: no password in any tool result, stderr capture or audit file of these runs.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class StdioServerTests(SqlServerFixture fixture)
{
    private static readonly string ServerDll = Path.Combine(
        typeof(StdioServerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ServerOutputDirectory").Value!,
        "mssql-readonly-mcp.dll");

    [Fact]
    public async Task Stdio_session_lists_three_tools_calls_each_and_is_audited()
    {
        Assert.True(File.Exists(ServerDll), "The server build output is missing.");
        var auditPath = fixture.NewAuditPath();
        var stderr = new ConcurrentQueue<string>();
        var texts = new List<string>();

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mssql-readonly-mcp",
            Command = "dotnet",
            Arguments = [ServerDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [ServerConfig.ConnectionStringVariable] = fixture.ReaderConnectionString,
                [ServerConfig.AuditLogVariable] = auditPath,
            },
            StandardErrorLines = stderr.Enqueue,
        });

        await using (var client = await McpClient.CreateAsync(transport))
        {
            var tools = await client.ListToolsAsync();
            Assert.Equal(["describe_table", "list_tables", "run_query"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
            Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, $"{t.Name} is not marked read-only"));
            Assert.All(tools, t => Assert.False(t.ProtocolTool.Annotations?.DestructiveHint ?? true, $"{t.Name} is not marked non-destructive"));

            var list = await client.CallToolAsync("list_tables", new Dictionary<string, object?> { ["name_filter"] = "Customer" });
            var describe = await client.CallToolAsync("describe_table", new Dictionary<string, object?> { ["table"] = "dbo.Orders" });
            var query = await client.CallToolAsync(
                "run_query", new Dictionary<string, object?> { ["sql"] = "SELECT TOP (3) Name FROM dbo.Customers ORDER BY CustomerId", ["max_rows"] = 2 });
            var refused = await client.CallToolAsync("run_query", new Dictionary<string, object?> { ["sql"] = "DELETE FROM dbo.Orders" });
            var failed = await client.CallToolAsync("run_query", new Dictionary<string, object?> { ["sql"] = "SELECT CAST(N'abc' AS int)" });

            foreach (var result in new[] { list, describe, query, refused, failed })
            {
                texts.Add(Text(result));
            }

            Assert.NotEqual(true, list.IsError);
            Assert.Contains("\"name\":\"Customers\"", Text(list), StringComparison.Ordinal);
            Assert.DoesNotContain("\"name\":\"Orders\"", Text(list), StringComparison.Ordinal);
            Assert.Contains("\"total_count\":2", Text(list), StringComparison.Ordinal);
            Assert.NotEqual(true, describe.IsError);
            Assert.Contains("FK_Orders_Customers", Text(describe), StringComparison.Ordinal);
            Assert.NotEqual(true, query.IsError);
            var rows = JsonDocument.Parse(Text(query)).RootElement;
            Assert.Equal(2, rows.GetProperty("row_count").GetInt32());
            Assert.Equal("max_rows", rows.GetProperty("truncated_reason").GetString());
            Assert.True(refused.IsError);
            Assert.StartsWith("NotSelect: refused by the read-only gate; ", Text(refused), StringComparison.Ordinal);
            Assert.True(failed.IsError);
            Assert.Contains("SqlError: SQL Server error 245, class 16", Text(failed), StringComparison.Ordinal);
        }

        var lines = TestText.AuditLines(auditPath);
        Assert.Equal(
            ["startup:allowed:Allowed", "list_tables:allowed:Allowed", "describe_table:allowed:Allowed", "run_query:allowed:Allowed",
             "run_query:refused:NotSelect", "run_query:error:SqlError"],
            lines.Select(l => $"{l.GetProperty("tool").GetString()}:{l.GetProperty("decision").GetString()}:{l.GetProperty("reason").GetString()}"));
        Assert.Equal("DELETE FROM dbo.Orders", lines[4].GetProperty("sql").GetString());
        Assert.True(AuditVerifier.Verify(auditPath).Ok);

        // R-8 for this session: tool results, stderr and the audit file.
        foreach (var text in texts.Concat(stderr).Append(TestText.ReadShared(auditPath)))
        {
            TestText.AssertNoSecrets(fixture, text);
        }

        Assert.DoesNotContain(stderr, line => line.Contains("SELECT", StringComparison.Ordinal) || line.Contains("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_failed_call_reaches_the_client_as_an_error_whose_text_starts_with_its_reason_word()
    {
        var auditPath = fixture.NewAuditPath();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mssql-readonly-mcp",
            Command = "dotnet",
            Arguments = [ServerDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [ServerConfig.ConnectionStringVariable] = fixture.ReaderConnectionString,
                [ServerConfig.AuditLogVariable] = auditPath,
            },
        });

        // One failing call per tool, and each kind of run_query failure a client can cause: a gate refusal, an argument
        // error, a SQL Server error. The text is what the client receives, exactly.
        var cases = new (string Tool, Dictionary<string, object?> Arguments, string Reason, string Start)[]
        {
            ("run_query", new() { ["sql"] = "DELETE FROM dbo.Orders" }, "NotSelect",
                "NotSelect: refused by the read-only gate; DeleteStatement at line 1, column 1"),
            ("run_query", new() { ["sql"] = "SELECT 1 AS one", ["max_rows"] = 0 }, "InvalidArgument",
                "InvalidArgument: max_rows must be 1 or more."),
            ("run_query", new() { ["sql"] = "SELECT CAST(N'abc' AS int) AS n" }, "SqlError",
                "SqlError: SQL Server error 245, class 16, line 1: "),
            ("list_tables", new() { ["schema"] = new string('s', 129) }, "InvalidArgument",
                "InvalidArgument: schema must be at most 128 characters."),
            ("describe_table", new() { ["table"] = "dbo.NoSuchTable" }, "NotFound",
                $"NotFound: no user table or view with that name in database {SqlServerFixture.Database}."),
        };

        var wrong = new List<string>();
        await using (var client = await McpClient.CreateAsync(transport))
        {
            foreach (var (tool, arguments, reason, start) in cases)
            {
                var result = await client.CallToolAsync(tool, arguments);
                var text = Text(result);
                TestText.AssertNoSecrets(fixture, text);
                var occurrences = text.Split(reason, StringSplitOptions.None).Length - 1;
                if (result.IsError != true || !text.StartsWith(start, StringComparison.Ordinal) || occurrences != 1)
                {
                    wrong.Add($"{tool} (isError {result.IsError}, reason {occurrences}x): {text}");
                }
            }

            // Calls the SDK rejects before a tool runs (a missing or mistyped argument) get the audit line's reason too.
            foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
            {
                ("describe_table", new()),
                ("run_query", new() { ["sql"] = "SELECT 1 AS one", ["max_rows"] = "many" }),
            })
            {
                var result = await client.CallToolAsync(tool, arguments);
                if (result.IsError != true || Text(result) != ToolErrors.InvalidRequestMessage)
                {
                    wrong.Add($"{tool} (isError {result.IsError}): {Text(result)}");
                }
            }

            // An unknown tool name is not a tool result at all: the SDK answers with a JSON-RPC error of its own.
            var unknown = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("no_such_tool", new Dictionary<string, object?>()).AsTask());
            Assert.Equal("Request failed (remote): Unknown tool: 'no_such_tool'", unknown.Message);
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.Equal(
            ["startup:allowed:Allowed", "run_query:refused:NotSelect", "run_query:error:InvalidArgument", "run_query:error:SqlError",
             "list_tables:error:InvalidArgument", "describe_table:error:NotFound", "describe_table:error:InvalidRequest",
             "run_query:error:InvalidRequest", "unknown:error:InvalidRequest"],
            TestText.AuditLines(auditPath).Select(Key));
    }

    [Fact]
    public async Task Started_with_sa_the_process_exits_3_with_one_stderr_line()
    {
        var auditPath = fixture.NewAuditPath();

        var run = await RunServerAsync([], fixture.SaConnectionString, auditPath);

        Assert.Equal(3, run.ExitCode);
        var line = Assert.Single(run.Stderr);
        Assert.StartsWith("mssql-readonly-mcp: refusing to start: the login holds rights beyond read-only access (", line, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
        var audit = Assert.Single(TestText.AuditLines(auditPath));
        Assert.Equal("startup:refused:LoginCanWrite", $"{audit.GetProperty("tool").GetString()}:{audit.GetProperty("decision").GetString()}:{audit.GetProperty("reason").GetString()}");
        AssertNoSecrets(run, auditPath);
    }

    [Fact]
    public async Task Login_whose_only_finding_widens_reading_is_refused_as_holding_rights_beyond_read_only_access()
    {
        // UNMASK changes no data, so the refusal wording must not claim it does.
        var connectionString = fixture.ExtraConnectionString(SqlServerFixture.UnmaskLogin);
        var auditPath = fixture.NewAuditPath();

        var start = await RunServerAsync([], connectionString, auditPath);
        var check = await RunServerAsync(["check-login"], connectionString, null);

        Assert.Equal(3, start.ExitCode);
        Assert.Equal(
            "mssql-readonly-mcp: refusing to start: the login holds rights beyond read-only access (1 finding, first: " +
            "UNMASK on database ShopDb). Run 'mssql-readonly-mcp check-login' for the full list.",
            Assert.Single(start.Stderr));
        Assert.Equal(3, check.ExitCode);
        Assert.Equal(
            [$"Login {SqlServerFixture.UnmaskLogin} on database ShopDb: refused, 1 right(s) outside the read-only set found:",
             "  UNMASK on database ShopDb"],
            check.Stdout);
        AssertNoSecrets(start, auditPath);
    }

    [Fact]
    public async Task Commands_report_and_exit_with_their_codes()
    {
        var reader = await RunServerAsync(["check-login"], fixture.ReaderConnectionString, null);
        var updater = await RunServerAsync(["check-login"], fixture.UpdaterConnectionString, null);
        var wrongPassword = await RunServerAsync(
            ["check-login"], fixture.ConnectionString(SqlServerFixture.ReaderLogin, fixture.ReaderPassword + "x"), null);
        var unreachable = await RunServerAsync(
            [], new SqlConnectionStringBuilder(fixture.ReaderConnectionString) { DataSource = "127.0.0.1,1", ConnectTimeout = 2 }.ConnectionString, fixture.NewAuditPath());
        var noConfig = await RunServerAsync([], null, null);
        var badLimit = await RunServerAsync([], fixture.ReaderConnectionString, fixture.NewAuditPath(), ("MSSQL_MCP_MAX_ROWS", "5001"));

        Assert.Equal(0, reader.ExitCode);
        Assert.Contains("passed", Assert.Single(reader.Stdout), StringComparison.Ordinal);
        Assert.Equal(3, updater.ExitCode);
        Assert.Contains(updater.Stdout, l => l.Trim() == "UPDATE on object [dbo].[Customers]");
        Assert.Equal(4, wrongPassword.ExitCode);
        Assert.Contains("number 18456", Assert.Single(wrongPassword.Stderr), StringComparison.Ordinal);
        Assert.Equal(4, unreachable.ExitCode);
        Assert.Single(unreachable.Stderr);
        Assert.Equal(2, noConfig.ExitCode);
        Assert.Equal("mssql-readonly-mcp: configuration error: MSSQL_CONNECTION_STRING is required.", Assert.Single(noConfig.Stderr));
        Assert.Equal(2, badLimit.ExitCode);
        Assert.Single(badLimit.Stderr);

        foreach (var run in new[] { reader, updater, wrongPassword, unreachable, noConfig, badLimit })
        {
            // The wrong password contains the reader's password, so this check covers it too.
            AssertNoSecrets(run, null);
        }

        // verify-audit on a file the server wrote, then on a tampered copy.
        using (var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString))
        {
            await harness.RunQueryAsync("SELECT 1 AS one");
        }

        var good = Directory.EnumerateFiles(fixture.WorkDirectory, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).First();
        var verified = await RunServerAsync(["verify-audit", good], null, null);
        Assert.Equal(0, verified.ExitCode);
        var tampered = Path.Combine(fixture.WorkDirectory, "tampered.jsonl");
        File.WriteAllText(tampered, File.ReadAllText(good).Replace("\"rows\":1", "\"rows\":2", StringComparison.Ordinal));
        var broken = await RunServerAsync(["verify-audit", tampered], null, null);
        Assert.Equal(1, broken.ExitCode);
    }

    [Fact]
    public async Task Connection_string_the_client_library_rejects_exits_2_with_one_line_from_the_type_name()
    {
        // SqlConnectionStringBuilder accepts this combination; SqlConnection refuses it (integrated authentication
        // with a password).
        var builder = new SqlConnectionStringBuilder(fixture.ReaderConnectionString)
        {
            Authentication = SqlAuthenticationMethod.ActiveDirectoryIntegrated,
        };

        var server = await RunServerAsync([], builder.ConnectionString, fixture.NewAuditPath());
        var check = await RunServerAsync(["check-login"], builder.ConnectionString, null);

        foreach (var run in new[] { server, check })
        {
            Assert.Equal(2, run.ExitCode);
            Assert.Equal(
                "mssql-readonly-mcp: configuration error: the SQL Server client library rejected MSSQL_CONNECTION_STRING (ArgumentException).",
                Assert.Single(run.Stderr));
            Assert.Empty(run.Stdout);
            AssertNoSecrets(run, null);
        }
    }

    [Fact]
    public async Task Second_server_on_the_same_audit_file_exits_2_with_one_stderr_line()
    {
        var auditPath = fixture.NewAuditPath();
        await using var first = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mssql-readonly-mcp",
            Command = "dotnet",
            Arguments = [ServerDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [ServerConfig.ConnectionStringVariable] = fixture.ReaderConnectionString,
                [ServerConfig.AuditLogVariable] = auditPath,
            },
        }));
        Assert.NotEmpty(await first.ListToolsAsync());

        var second = await RunServerAsync([], fixture.ReaderConnectionString, auditPath);

        Assert.Equal(2, second.ExitCode);
        var line = Assert.Single(second.Stderr);
        Assert.StartsWith("mssql-readonly-mcp: audit log error: Audit file ", line, StringComparison.Ordinal);
        Assert.Contains("locked by another writer", line, StringComparison.Ordinal);
        Assert.Empty(second.Stdout);
        AssertNoSecrets(second, auditPath);
    }

    [Fact]
    public async Task Calls_the_sdk_rejects_before_a_tool_runs_are_audited()
    {
        var auditPath = fixture.NewAuditPath();
        var stderr = new ConcurrentQueue<string>();
        var outcomes = new List<string>();
        await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mssql-readonly-mcp",
            Command = "dotnet",
            Arguments = [ServerDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [ServerConfig.ConnectionStringVariable] = fixture.ReaderConnectionString,
                [ServerConfig.AuditLogVariable] = auditPath,
            },
            StandardErrorLines = stderr.Enqueue,
        })))
        {
            foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
            {
                ("no_such_tool", new() { ["sql"] = "SELECT 1" }),
                ("run_query", new()),
                ("run_query", new() { ["sql"] = "SELECT 1 AS one", ["max_rows"] = "many" }),
                ("describe_table", new() { ["table"] = 42 }),
                ("run_query", new() { ["sql"] = "SELECT 1 AS one" }),
            })
            {
                try
                {
                    var result = await client.CallToolAsync(tool, arguments);
                    outcomes.Add($"{tool}: {(result.IsError == true ? "error" : "ok")}");
                }
                catch (McpException exception)
                {
                    outcomes.Add($"{tool}: protocol error {exception.GetType().Name}");
                }
            }
        }

        var lines = TestText.AuditLines(auditPath);
        Assert.True(
            lines.Count == 6,
            $"expected one line per call plus startup; calls: {string.Join(" | ", outcomes)}; lines: {string.Join(" | ", lines.Select(Key))}");
        Assert.Equal(
            ["startup:allowed:Allowed", "unknown:error:InvalidRequest", "run_query:error:InvalidRequest",
             "run_query:error:InvalidRequest", "describe_table:error:InvalidRequest", "run_query:allowed:Allowed"],
            lines.Select(Key));
        Assert.All(lines.Skip(1).Take(4), line =>
        {
            Assert.Equal(JsonValueKind.Null, line.GetProperty("sql").ValueKind);
            Assert.Empty(line.GetProperty("tables").EnumerateArray());
        });
        Assert.True(AuditVerifier.Verify(auditPath).Ok);
        Assert.DoesNotContain(stderr, l => l.Contains("SELECT", StringComparison.Ordinal));

        // One warning per call that failed before a tool ran: the tool and the exception type, no message, no argument.
        Assert.Equal(
            ["warn: MssqlReadOnlyMcp.ToolService[0] unknown: rejected before the tool ran (McpProtocolException)",
             "warn: MssqlReadOnlyMcp.ToolService[0] run_query: rejected before the tool ran (ArgumentException)",
             "warn: MssqlReadOnlyMcp.ToolService[0] run_query: rejected before the tool ran (JsonException)",
             "warn: MssqlReadOnlyMcp.ToolService[0] describe_table: rejected before the tool ran (JsonException)"],
            stderr.Where(l => l.StartsWith("warn: ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Requests whose parameters the SDK cannot deserialize, so the call-tool filter never runs (security finding X-1):
    /// a lone surrogate (the JSON escape <c>\ud800</c>, sent as written) in an argument, arguments that are not an
    /// object, no tool name. Each still gets exactly one audit line, on disk before its answer arrives.
    /// </summary>
    [Theory]
    [InlineData("describe_table", """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"describe_table","arguments":{"table":"dbo.Customers\ud800"}}}""", "InvalidOperationException")]
    [InlineData("run_query", """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"run_query","arguments":{"sql":"SELECT N'\ud800' AS v"}}}""", "InvalidOperationException")]
    [InlineData("run_query", """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"run_query","arguments":"SELECT 1"}}""", "JsonException")]
    [InlineData("unknown", """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"arguments":{"sql":"SELECT 1"}}}""", "JsonException")]
    [InlineData("list_tables", """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_tables","arguments":{"schema":"dbo\ud800"}}}""", "InvalidOperationException")]
    public async Task Call_whose_parameters_the_sdk_cannot_read_is_audited_before_its_error_answer(string tool, string request, string error)
    {
        var auditPath = fixture.NewAuditPath();
        await using var server = RawStdioServer.Start(ServerDll, fixture.ReaderConnectionString, auditPath);
        await server.InitializeAsync();

        var response = await server.SendLineAsync(2, request);
        var linesAtAnswer = TestText.AuditLines(auditPath);
        var run = await server.CloseInputAndWaitAsync(TimeSpan.FromSeconds(60));

        // The answer stays the SDK's generic JSON-RPC error: no detail, no data.
        Assert.False(response.TryGetProperty("result", out _), response.GetRawText());
        var answer = response.GetProperty("error");
        Assert.Equal(-32603, answer.GetProperty("code").GetInt32());
        Assert.Equal("An error occurred.", answer.GetProperty("message").GetString());
        Assert.False(answer.TryGetProperty("data", out _), response.GetRawText());

        Assert.Equal(["startup:allowed:Allowed", $"{tool}:error:InvalidRequest"], linesAtAnswer.Select(Key));
        var line = linesAtAnswer[1];
        Assert.Equal(JsonValueKind.Null, line.GetProperty("sql").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("sql_sha256").ValueKind);
        Assert.Empty(line.GetProperty("tables").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("rows").ValueKind);
        Assert.False(line.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("result_sha256").ValueKind);
        Assert.Equal(2, TestText.AuditLines(auditPath).Count);

        var verify = await RunServerAsync(["verify-audit", auditPath], null, null);
        Assert.True(verify.ExitCode == 0, string.Join(" | ", verify.Stdout.Concat(verify.Stderr)));

        // The usual two stderr lines of a call rejected before a tool ran: the tool and the exception type only.
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}; stderr: {string.Join(" | ", run.Stderr)}");
        Assert.True(run.Stderr.Length == 2, string.Join(" | ", run.Stderr));
        Assert.Matches($"^info: MssqlReadOnlyMcp\\.ToolService\\[0\\] {tool} error InvalidRequest [0-9]+ ms$", run.Stderr[0]);
        Assert.Equal($"warn: MssqlReadOnlyMcp.ToolService[0] {tool}: rejected before the tool ran ({error})", run.Stderr[1]);
        AssertNoSecrets(new ProcessRun(run.ExitCode, run.Stdout, run.Stderr), auditPath);
    }

    [Fact]
    public async Task Setup_script_creates_a_login_that_passes_check_login_and_runs_twice()
    {
        var root = typeof(StdioServerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "RepositoryRoot").Value!;
        var script = await File.ReadAllBytesAsync(Path.Combine(root, "scripts", "create-readonly-login.sql"));
        const string login = "mcp_script_reader";
        var environment = $"MCP_LOGIN={login}\nMCP_DATABASE={SqlServerFixture.ExampleDatabase}\nMCP_PASSWORD={fixture.ScriptLoginPassword}\n";
        await fixture.CopyToContainerAsync(script, "/tmp/create-readonly-login.sql");
        await fixture.CopyToContainerAsync(System.Text.Encoding.UTF8.GetBytes(environment), "/tmp/create-readonly-login.env");

        // The variables reach sqlcmd as environment variables (as the script asks); the administrator's password
        // reaches it as SQLCMDPASSWORD. Nothing secret is on a command line.
        string[] command =
        [
            "/bin/bash", "-c",
            "set -a; . /tmp/create-readonly-login.env; set +a; SQLCMDPASSWORD=\"$MSSQL_SA_PASSWORD\" " +
            "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -i /tmp/create-readonly-login.sql",
        ];
        try
        {
            var firstRun = await fixture.ExecInContainerAsync(command);
            Assert.True(firstRun.ExitCode == 0, $"first run exit {firstRun.ExitCode}: {firstRun.Stdout} {firstRun.Stderr}");
            Assert.Contains($"Created login {login}.", firstRun.Stdout, StringComparison.Ordinal);

            // No database in the connection string: the login lands in the default database the script set.
            var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString(login, fixture.ScriptLoginPassword));
            connectionString.Remove("Initial Catalog");
            var check = await RunServerAsync(["check-login"], connectionString.ConnectionString, null);
            Assert.True(check.ExitCode == 0, string.Join(" | ", check.Stdout.Concat(check.Stderr)));
            Assert.Equal(
                $"Login {login} on database {SqlServerFixture.ExampleDatabase}: passed, no permission to change data was found.",
                Assert.Single(check.Stdout));

            var secondRun = await fixture.ExecInContainerAsync(command);
            Assert.True(secondRun.ExitCode == 0, $"second run exit {secondRun.ExitCode}: {secondRun.Stdout} {secondRun.Stderr}");
            Assert.Contains($"Login {login} exists; its password was not changed.", secondRun.Stdout, StringComparison.Ordinal);
            Assert.Contains("reused", secondRun.Stdout, StringComparison.Ordinal);

            foreach (var text in new[] { firstRun.Stdout, firstRun.Stderr, secondRun.Stdout, secondRun.Stderr })
            {
                TestText.AssertNoSecrets(fixture, text);
            }

            AssertNoSecrets(check, null);
        }
        finally
        {
            await fixture.ExecInContainerAsync(["/bin/rm", "-f", "/tmp/create-readonly-login.env"]);
        }
    }

    [Fact]
    public async Task Client_closing_stdin_after_a_call_ends_the_process_with_exit_0_and_no_error_line()
    {
        var auditPath = fixture.NewAuditPath();
        await using var server = RawStdioServer.Start(ServerDll, fixture.ReaderConnectionString, auditPath);
        await server.InitializeAsync();

        var response = await server.CallAsync(2, "run_query", new { sql = "SELECT TOP (2) Name FROM dbo.Customers ORDER BY CustomerId" });
        Assert.False(response.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean(), response.GetRawText());

        var run = await server.CloseInputAndWaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}; stderr: {string.Join(" | ", run.Stderr)}");
        AssertOnlyCallLogLines(run.Stderr);
        Assert.Equal(["startup:allowed:Allowed", "run_query:allowed:Allowed"], TestText.AuditLines(auditPath).Select(Key));
        var verify = AuditVerifier.Verify(auditPath);
        Assert.True(verify.Ok, verify.Message);
        AssertNoSecrets(new ProcessRun(run.ExitCode, run.Stdout, run.Stderr), auditPath);
    }

    [Fact]
    public async Task Call_in_flight_when_stdin_closes_writes_its_audit_line_before_the_process_ends()
    {
        var auditPath = fixture.NewAuditPath();
        await using var admin = new SqlConnection(fixture.ConnectionString("sa", fixture.SaPassword, SqlServerFixture.SalesDatabase));
        await admin.OpenAsync();
        var transaction = (SqlTransaction)await admin.BeginTransactionAsync();
        await using (var hold = new SqlCommand("UPDATE dbo.LockLab SET V = V + 1 WHERE Id = 2;", admin, transaction))
        {
            await hold.ExecuteNonQueryAsync();
        }

        await using var server = RawStdioServer.Start(
            ServerDll,
            fixture.SalesReaderConnectionString,
            auditPath,
            (ServerConfig.LockTimeoutVariable, "60000"),
            (ServerConfig.TimeoutVariable, "60"));
        await server.InitializeAsync();
        server.Send(2, "tools/call", new { name = "run_query", arguments = new { sql = "SELECT V FROM dbo.LockLab WHERE Id = 2" } });
        await WaitForBlockedReaderAsync();

        // End of input while the call waits for the lock.
        var exit = server.CloseInputAndWaitAsync(TimeSpan.FromSeconds(90));
        var stillRunning = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(3))) != exit;
        await transaction.RollbackAsync();
        var run = await exit;

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}; stderr: {string.Join(" | ", run.Stderr)}");
        AssertOnlyCallLogLines(run.Stderr);
        var lines = TestText.AuditLines(auditPath);
        Assert.Equal(2, lines.Count);
        Assert.Equal("SELECT V FROM dbo.LockLab WHERE Id = 2", lines[1].GetProperty("sql").GetString());
        var verify = AuditVerifier.Verify(auditPath);
        Assert.True(verify.Ok, verify.Message);

        // What the SDK 2.4.0 does with a call in flight at end of input: it does not cancel it. The process stays up
        // while the call waits for the lock, the call finishes once the lock is released, and its line is the
        // ordinary one.
        Assert.True(stillRunning, "the process ended while the call was still waiting for the lock");
        Assert.Equal("run_query:allowed:Allowed", Key(lines[1]));
    }

    [Fact]
    public async Task Call_stalled_at_connection_open_ends_at_the_time_limit_of_the_call_and_is_audited()
    {
        // The server reaches SQL Server through a proxy owned by this test. After startup the proxy accepts new
        // connections and never answers them. The connect timeout (20 s) is longer than the drain window
        // (MSSQL_MCP_TIMEOUT_SECONDS + 10 s = 12 s), but the call's time limit (2 s) covers opening the connection
        // too, so the call ends at that limit, before the drain window.
        const int ConnectTimeoutSeconds = 20;
        const int DrainWindowSeconds = 2 + 10;
        await using var proxy = StallingProxy.Start(fixture.SalesReaderConnectionString);
        var connectionString = new SqlConnectionStringBuilder(fixture.SalesReaderConnectionString)
        {
            DataSource = proxy.DataSource,
            Pooling = false,
            ConnectTimeout = ConnectTimeoutSeconds,
        }.ConnectionString;
        var auditPath = fixture.NewAuditPath();
        await using var server = RawStdioServer.Start(ServerDll, connectionString, auditPath, (ServerConfig.TimeoutVariable, "2"));
        await server.InitializeAsync();

        proxy.StallNewConnections();
        server.Send(2, "tools/call", new { name = "run_query", arguments = new { sql = "SELECT 1 AS one" } });
        await proxy.FirstStalledConnection.WaitAsync(TimeSpan.FromSeconds(30));

        var watch = Stopwatch.StartNew();
        var run = await server.CloseInputAndWaitAsync(TimeSpan.FromSeconds(90));
        watch.Stop();

        // What the SDK 2.4.0 does at end of input: it keeps the host running until the call ends on its own (the
        // host's shutdown timeout does not apply; the host is not stopping yet). The call ends at its time limit,
        // not at the connect timeout, so the process exits 0 inside the drain window.
        Assert.True(run.ExitCode == ExitCodes.Ok, $"exit {run.ExitCode} after {watch.Elapsed}; stderr: {string.Join(" | ", run.Stderr)}");
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(DrainWindowSeconds),
            $"the process ended {watch.Elapsed} after end of input, past the drain window");
        AssertOnlyCallLogLines(run.Stderr);
        var lines = TestText.AuditLines(auditPath);
        Assert.Equal(2, lines.Count);
        // The stalled connection open ends at the call's time limit, answered and audited as a timeout.
        Assert.Equal("run_query:error:Timeout", Key(lines[1]));
        var verify = AuditVerifier.Verify(auditPath);
        Assert.True(verify.Ok, verify.Message);
        AssertNoSecrets(new ProcessRun(run.ExitCode, run.Stdout, run.Stderr), auditPath);
    }

    [Fact]
    public async Task Reader_sees_in_sys_databases_a_database_it_has_no_user_in()
    {
        await using (var admin = new SqlConnection(fixture.ConnectionString("sa", fixture.SaPassword, SqlServerFixture.OtherDatabase)))
        {
            await admin.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM sys.database_principals WHERE sid = SUSER_SID(@login)", admin);
            command.Parameters.AddWithValue("@login", SqlServerFixture.ReaderLogin);
            Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
        }

        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var names = await DatabaseNamesAsync(harness);

        // VIEW ANY DATABASE, which public holds by default, lists every database name on the instance.
        Assert.Contains(SqlServerFixture.OtherDatabase, names);
        Assert.Contains(SqlServerFixture.ExampleDatabase, names);
        Assert.Contains(SqlServerFixture.SalesDatabase, names);
    }

    [Fact]
    public async Task Deny_view_any_database_hides_other_databases_and_the_server_still_works()
    {
        var connectionString = fixture.ExtraConnectionString(SqlServerFixture.NoDatabaseListLogin);

        var check = await RunServerAsync(["check-login"], connectionString, null);
        Assert.True(check.ExitCode == 0, string.Join(" | ", check.Stdout.Concat(check.Stderr)));

        using var harness = await Harness.StartAsync(fixture, connectionString);
        var listed = await harness.ListTablesAsync();
        var described = await harness.DescribeTableAsync("dbo.Orders");
        Assert.False(listed.IsError, listed.Error);
        Assert.False(described.IsError, described.Error);

        // What remains: master, tempdb and the database the session is in.
        Assert.Equal(["master", SqlServerFixture.Database, "tempdb"], await DatabaseNamesAsync(harness));
    }

    private static async Task<List<string>> DatabaseNamesAsync(Harness harness)
    {
        var outcome = await harness.RunQueryAsync("SELECT name FROM sys.databases ORDER BY name", maxRows: 100);
        Assert.False(outcome.IsError, outcome.Error);
        Assert.False(outcome.Json.GetProperty("truncated").GetBoolean());
        return outcome.Json.GetProperty("rows").EnumerateArray().Select(row => row[0].GetString()!).ToList();
    }

    private async Task WaitForBlockedReaderAsync()
    {
        await using var watcher = new SqlConnection(fixture.ConnectionString("sa", fixture.SaPassword, SqlServerFixture.SalesDatabase));
        await watcher.OpenAsync();
        var watch = Stopwatch.StartNew();
        while (true)
        {
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_requests AS r JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id " +
                "WHERE s.login_name = @login AND r.wait_type LIKE N'LCK%'",
                watcher);
            command.Parameters.AddWithValue("@login", SqlServerFixture.ReaderLogin);
            if ((int)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the call never reached SQL Server");
            await Task.Delay(50);
        }
    }

    /// <summary>A normal run writes one information line per tool call to stderr and nothing else.</summary>
    private static void AssertOnlyCallLogLines(IEnumerable<string> stderr) =>
        Assert.All(stderr, line => Assert.StartsWith("info: MssqlReadOnlyMcp.ToolService", line, StringComparison.Ordinal));

    private static string Key(JsonElement line) =>
        $"{line.GetProperty("tool").GetString()}:{line.GetProperty("decision").GetString()}:{line.GetProperty("reason").GetString()}";

    /// <summary>
    /// The server process driven with raw JSON-RPC lines, so the test decides when standard input closes and reads the
    /// exit code and the whole of stderr.
    /// </summary>
    private sealed class RawStdioServer : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> stderr;
        private readonly System.Threading.Channels.Channel<string> stdout = System.Threading.Channels.Channel.CreateUnbounded<string>();
        private readonly List<string> stdoutLines = [];
        private readonly Task pump;

        private RawStdioServer(Process process)
        {
            this.process = process;
            stderr = process.StandardError.ReadToEndAsync();
            pump = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    lock (stdoutLines)
                    {
                        stdoutLines.Add(line);
                    }

                    await stdout.Writer.WriteAsync(line);
                }

                stdout.Writer.Complete();
            });
        }

        public static RawStdioServer Start(string serverDll, string connectionString, string auditPath, params (string Name, string Value)[] extra)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(serverDll);
            foreach (var name in new[]
            {
                ServerConfig.MaxRowsVariable, ServerConfig.MaxCharsVariable, ServerConfig.TimeoutVariable, ServerConfig.LockTimeoutVariable,
            })
            {
                start.Environment.Remove(name);
            }

            start.Environment[ServerConfig.ConnectionStringVariable] = connectionString;
            start.Environment[ServerConfig.AuditLogVariable] = auditPath;
            foreach (var (name, value) in extra)
            {
                start.Environment[name] = value;
            }

            return new RawStdioServer(Process.Start(start)!);
        }

        public async Task InitializeAsync()
        {
            Send(1, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "shutdown-test", version = "1.0" },
            });
            await ReadResponseAsync(1);
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.FlushAsync();
        }

        public async Task<JsonElement> CallAsync(int id, string tool, object arguments)
        {
            Send(id, "tools/call", new { name = tool, arguments });
            return await ReadResponseAsync(id);
        }

        public void Send(int id, string method, object parameters)
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            process.StandardInput.Flush();
        }

        /// <summary>Sends one line exactly as given (a request no serializer would write) and reads the answer to <paramref name="id"/>.</summary>
        public async Task<JsonElement> SendLineAsync(int id, string line)
        {
            await process.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
            return await ReadResponseAsync(id);
        }

        public async Task<(int ExitCode, string[] Stdout, string[] Stderr)> CloseInputAndWaitAsync(TimeSpan timeout)
        {
            process.StandardInput.Close();
            using var cancel = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancel.Token);
            await pump;
            string[] lines;
            lock (stdoutLines)
            {
                lines = [.. stdoutLines];
            }

            return (process.ExitCode, lines, Lines(await stderr));
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
        }

        private async Task<JsonElement> ReadResponseAsync(int id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                var line = await stdout.Reader.ReadAsync(timeout.Token);
                var message = JsonDocument.Parse(line).RootElement;
                if (message.TryGetProperty("id", out var messageId) && messageId.ValueKind == JsonValueKind.Number && messageId.GetInt32() == id)
                {
                    return message.Clone();
                }
            }
        }
    }

    /// <summary>
    /// A TCP proxy in front of the SQL Server container. It forwards until <see cref="StallNewConnections"/>; after that
    /// it accepts new connections and never answers them, so a connection open waits for its connect timeout.
    /// </summary>
    private sealed class StallingProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly string targetHost;
        private readonly int targetPort;
        private readonly CancellationTokenSource stop = new();
        private readonly List<TcpClient> clients = [];
        private readonly TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task accept = Task.CompletedTask;
        private volatile bool stalling;

        private StallingProxy(string targetHost, int targetPort)
        {
            this.targetHost = targetHost;
            this.targetPort = targetPort;
        }

        /// <summary>The <c>Data Source</c> that reaches SQL Server through this proxy.</summary>
        public string DataSource => $"127.0.0.1,{((IPEndPoint)listener.LocalEndpoint).Port}";

        /// <summary>Completes when the first connection after <see cref="StallNewConnections"/> arrives.</summary>
        public Task FirstStalledConnection => stalled.Task;

        public static StallingProxy Start(string connectionString)
        {
            var target = new SqlConnectionStringBuilder(connectionString).DataSource.Split(',');
            var proxy = new StallingProxy(target[0].Replace("tcp:", string.Empty, StringComparison.Ordinal), int.Parse(target[1], CultureInfo.InvariantCulture));
            proxy.listener.Start();
            proxy.accept = Task.Run(proxy.AcceptAsync);
            return proxy;
        }

        public void StallNewConnections() => stalling = true;

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            lock (clients)
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }

            try
            {
                await accept;
            }
            catch (OperationCanceledException)
            {
            }

            stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                lock (clients)
                {
                    clients.Add(client);
                }

                if (stalling)
                {
                    stalled.TrySetResult();
                }
                else
                {
                    _ = ForwardAsync(client);
                }
            }
        }

        private async Task ForwardAsync(TcpClient client)
        {
            var upstream = new TcpClient();
            lock (clients)
            {
                clients.Add(upstream);
            }

            try
            {
                await upstream.ConnectAsync(targetHost, targetPort, stop.Token);
                var down = client.GetStream();
                var up = upstream.GetStream();
                await Task.WhenAny(down.CopyToAsync(up, stop.Token), up.CopyToAsync(down, stop.Token));
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // The connection ended or the proxy stopped.
            }
            finally
            {
                client.Dispose();
                upstream.Dispose();
            }
        }
    }

    private void AssertNoSecrets(ProcessRun run, string? auditPath)
    {
        foreach (var text in run.Stdout.Concat(run.Stderr))
        {
            TestText.AssertNoSecrets(fixture, text);
        }

        if (auditPath is not null && File.Exists(auditPath))
        {
            TestText.AssertNoSecrets(fixture, TestText.ReadShared(auditPath));
        }
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task<ProcessRun> RunServerAsync(
        string[] arguments, string? connectionString, string? auditPath, params (string Name, string Value)[] extra)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ServerDll);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in new[]
        {
            ServerConfig.ConnectionStringVariable, ServerConfig.AuditLogVariable, ServerConfig.MaxRowsVariable,
            ServerConfig.MaxCharsVariable, ServerConfig.TimeoutVariable, ServerConfig.LockTimeoutVariable,
        })
        {
            start.Environment.Remove(name);
        }

        if (connectionString is not null)
        {
            start.Environment[ServerConfig.ConnectionStringVariable] = connectionString;
        }

        if (auditPath is not null)
        {
            start.Environment[ServerConfig.AuditLogVariable] = auditPath;
        }

        foreach (var (name, value) in extra)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessRun(process.ExitCode, Lines(await stdout), Lines(await stderr));
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    private sealed record ProcessRun(int ExitCode, string[] Stdout, string[] Stderr);
}
