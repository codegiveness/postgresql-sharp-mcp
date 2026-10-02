using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;

namespace PostgreSqlMcp.Verify;

internal static class FuzzChecks
{
    public static async Task RunAsync(McpClient client)
    {
        var random = new Random(0x50474D43);
        string[] fragments = ["a", "Z", "0", " ", "'", "\"", "\\", ";", "$", "/*", "*/", "--", "\r\n", "é", "界", "🧪"];
        var payloads = Arb.Array(Arb.From(Gen.Elements(fragments)));
        var depths = Arb.From(Gen.Choose(1, 5), depth => Enumerable.Range(1, depth - 1));
        const int casesPerEncoding = 43;
        for (int encoding = 0; encoding < 3; encoding++)
        {
            int quoteKind = encoding;
            string[]? failedParts = null;
            int failedDepth = 0;
            var property = Prop.ForAll(payloads, depths, async (parts, depth) =>
            {
                try
                {
                    string expected = "literal; COMMIT; SELECT " + string.Concat(parts);
                    string literal = quoteKind switch
                    {
                        0 => "'" + expected.Replace("'", "''", StringComparison.Ordinal) + "'",
                        1 => "E'" + expected.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'",
                        _ => "$mcp_literal$" + expected + "$mcp_literal$"
                    };
                    string comment = string.Concat(Enumerable.Repeat("/* ignored SELECT; ", depth))
                        + string.Concat(Enumerable.Repeat(" COMMIT; */", depth));
                    string sql = $"{comment}\nSELECT {literal} AS value; -- ignored RESET;\n{comment}";
                    var result = await client.OkAsync("execute_sql", new { database = "a", sql });
                    Check.Equal(result["rows"]![0]![0], expected, "Quoted SQL property changed the returned value.");
                    Check.That(!result["truncated"].Flag(), "Small SQL property value was unexpectedly truncated.");
                    await client.FailsAsync("execute_sql", new { database = "a", sql = sql + "\nSELECT 987654321" }, "invalid_sql");
                    return true;
                }
                catch
                {
                    failedParts = parts;
                    failedDepth = depth;
                    throw;
                }
            });
            try
            {
                FsCheck.Check.One(Config.QuickThrowOnFailure
                    .WithName($"PostgreSQL literal encoding {quoteKind}")
                    .WithMaxTest(casesPerEncoding).WithEndSize(24)
                    .WithReplay(0x50474D43UL + (ulong)quoteKind, 1UL), property);
            }
            catch (Exception) when (failedParts is not null)
            {
                // Only generated inputs are safe to report; nested provider/process errors may contain credentials.
                throw new VerificationException($"SQL property failed after shrinking: encoding={quoteKind}, depth={failedDepth}, fragments={JsonSerializer.Serialize(failedParts)}. Replay seed={0x50474D43UL + (ulong)quoteKind}, gamma=1.");
            }
        }

        string[] controls = ["BEGIN", "START", "COMMIT", "END", "ROLLBACK", "ABORT", "SAVEPOINT", "RELEASE", "PREPARE", "SET", "RESET", "DISCARD", "COPY", "DO", "CALL", "VACUUM"];
        foreach (string control in controls)
        {
            var mixed = new StringBuilder(control.Length);
            foreach (char character in control)
                mixed.Append(random.Next(2) == 0 ? char.ToLowerInvariant(character) : character);
            await client.FailsAsync("execute_sql", new { database = "a", sql = $"/* SELECT; /* quoted ' */ */\n{mixed}" }, "invalid_sql");
        }
        foreach (string sql in new[] { "SELECT 'unterminated", "SELECT E'escaped\\'", "SELECT \"unterminated", "SELECT $tag$unterminated", "SELECT 1 /* nested /* closed */", "SELECT '\0'", "SELECT '" + new string('x', 131072) + "'" })
            await client.FailsAsync("execute_sql", new { database = "a", sql }, "invalid_sql");

        Console.WriteLine($"PASS FsCheck SQL properties: {casesPerEncoding * 3} PostgreSQL literal round trips and second-statement rejections with shrinking, nested comments, mixed-case controls and malformed boundaries");
    }
}
