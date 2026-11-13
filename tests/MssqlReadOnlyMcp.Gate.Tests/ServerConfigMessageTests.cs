namespace MssqlReadOnlyMcp.Gate.Tests;

/// <summary>
/// I-2: the configuration error for <c>MSSQL_CONNECTION_STRING</c> tells the user what to check, and never holds any
/// part of the value. No SQL Server is involved.
/// </summary>
public sealed class ServerConfigMessageTests
{
    private const string Required = "MSSQL_CONNECTION_STRING is required";

    [Fact]
    public void Unset_value_is_reported_as_required()
    {
        Assert.Equal(Required, Fail(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t \r\n")]
    public void Empty_or_whitespace_value_says_it_is_empty(string value)
    {
        string message = Fail(value);
        Assert.Equal(ServerConfig.EmptyConnectionStringMessage, message);
        Assert.StartsWith("MSSQL_CONNECTION_STRING is set but empty: ", message, StringComparison.Ordinal);
        Assert.Contains("README step 2, \"Create and check the read-only login\"", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("${user_config.connection_string}")]
    [InlineData("  ${user_config.connection_string}\n")]
    [InlineData("${}")]
    [InlineData("${input:mssql-connection}")]
    public void Unfilled_placeholder_says_the_client_did_not_fill_it_in(string value)
    {
        string message = Fail(value);
        Assert.Equal(ServerConfig.PlaceholderConnectionStringMessage, message);
        Assert.Contains("the client did not fill it in", message, StringComparison.Ordinal);
        Assert.DoesNotContain("user_config", message, StringComparison.Ordinal);
        Assert.DoesNotContain("input:", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server=db01;Database=Sales;User ID=reader;Password=S3cr3t-Pa55;Bogus Key=1")]
    [InlineData("$env:MSSQL_CONNECTION_STRING = \"Server=db01;Password=S3cr3t-Pa55\"")]
    [InlineData("mssql://reader:S3cr3t-Pa55@db01/Sales")]
    [InlineData("${user_config.connection_string")]
    [InlineData("Server=db01;Password='S3cr3t-Pa55")]
    public void Unparsable_value_says_what_form_is_expected(string value)
    {
        string message = Fail(value);
        Assert.Equal(ServerConfig.InvalidConnectionStringMessage, message);
        Assert.Contains("keyword=value pairs separated by semicolons", message, StringComparison.Ordinal);
        Assert.Contains("Server, Database", message, StringComparison.Ordinal);
        Assert.Contains("README step 2, \"Create and check the read-only login\"", message, StringComparison.Ordinal);
        foreach (string part in new[] { "S3cr3t", "Pa55", "db01", "Sales", "reader", "Bogus", "mssql://", "$env" })
        {
            Assert.DoesNotContain(part, message, StringComparison.Ordinal);
        }
    }

    // No character of a secret-looking input reaches any message: the input is built only from characters that none
    // of the fixed messages holds, and each message is checked character by character.
    [Fact]
    public void No_character_of_a_secret_looking_value_reaches_the_message()
    {
        const string secret = "Ж§ΨÆ¤€ßµ¶Ω·Ł";
        string[] inputs = [secret, "${" + secret + "}", " " + secret + " ", "Ж" + secret + "Ж"];
        foreach (string input in inputs)
        {
            string message = Fail(input);
            foreach (char c in input.Where(c => !char.IsWhiteSpace(c) && c is not '$' and not '{' and not '}'))
            {
                Assert.DoesNotContain(c, message);
            }

            Assert.All(message, c => Assert.True(c < 128, "The message holds a non-ASCII character."));
        }

        // The message is a fixed text: two different unparsable values give the same one.
        Assert.Equal(Fail("Ж" + secret), Fail("xyz-not-a-connection-string"));
    }

    [Fact]
    public void Valid_value_still_loads()
    {
        var config = ServerConfig.Load(
            name => name == ServerConfig.ConnectionStringVariable ? "Server=db01;Database=Sales;User ID=reader;Password=p" : null,
            requireAuditLog: false);
        Assert.Contains("Application Name=mssql-readonly-mcp", config.ConnectionString, StringComparison.Ordinal);
    }

    // The server and check-login print the message as one stderr line, exit 2, and print nothing of the value.
    [Theory]
    [InlineData("${user_config.connection_string}")]
    [InlineData("Server=db01;Password=S3cr3t-Pa55;Bogus Key=1")]
    [InlineData("   ")]
    public async Task Server_and_check_login_print_the_message_as_one_line(string value)
    {
        string expected = "mssql-readonly-mcp: configuration error: " + Fail(value) + ".";
        foreach (string[] arguments in new[] { Array.Empty<string>(), ["check-login"] })
        {
            var (exitCode, stderr) = await ServerProcess.RunAsync(
                arguments,
                new Dictionary<string, string>
                {
                    [ServerConfig.ConnectionStringVariable] = value,
                    [ServerConfig.AuditLogVariable] = Path.Combine(Path.GetTempPath(), "never-opened-" + Guid.NewGuid().ToString("N") + ".jsonl"),
                });
            Assert.Equal(2, exitCode);
            Assert.Equal(expected, Assert.Single(stderr));
            Assert.DoesNotContain("S3cr3t", stderr[0], StringComparison.Ordinal);
            Assert.DoesNotContain("user_config", stderr[0], StringComparison.Ordinal);
        }
    }

    private static string Fail(string? connectionString)
    {
        var ex = Assert.Throws<ConfigException>(() => ServerConfig.Load(
            name => name switch
            {
                ServerConfig.ConnectionStringVariable => connectionString,
                ServerConfig.AuditLogVariable => "audit.jsonl",
                _ => null,
            },
            requireAuditLog: true));
        return ex.Message;
    }
}
