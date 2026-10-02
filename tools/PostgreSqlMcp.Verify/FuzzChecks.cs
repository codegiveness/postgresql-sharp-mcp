using System.Text;

namespace PostgreSqlMcp.Verify;

internal static class FuzzChecks
{
    public static async Task RunAsync(McpClient client)
    {
        var random = new Random(0x50474D43);
        string[] fragments = ["a", "Z", "0", " ", "'", "\"", "\\", ";", "$", "/*", "*/", "--", "\r\n", "é", "界", "🧪"];
        const int cases = 128;
        for (int index = 0; index < cases; index++)
        {
            var value = new StringBuilder("literal; COMMIT; SELECT ");
            for (int part = random.Next(1, 25); part > 0; part--)
                value.Append(fragments[random.Next(fragments.Length)]);
            string expected = value.ToString();
            string tag = $"$mcp_case_{index}$";
            string literal = (index % 3) switch
            {
                0 => "'" + expected.Replace("'", "''", StringComparison.Ordinal) + "'",
                1 => "E'" + expected.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'",
                _ => tag + expected + tag
            };
            int depth = random.Next(1, 6);
            string comment = string.Concat(Enumerable.Repeat("/* ignored SELECT; ", depth))
                + string.Concat(Enumerable.Repeat(" COMMIT; */", depth));
            string sql = $"{comment}\nSELECT {literal} AS value; -- ignored RESET;\n{comment}";
            var result = await client.OkAsync("execute_sql", new { database = "a", sql });
            Check.Equal(result["rows"]![0]![0], expected, "Quoted SQL fuzz case changed the returned value.");
            Check.That(!result["truncated"].Flag(), "Small SQL fuzz value was unexpectedly truncated.");
            await client.FailsAsync("execute_sql", new { database = "a", sql = sql + "\nSELECT 987654321" }, "invalid_sql");
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

        Console.WriteLine($"PASS deterministic SQL fuzzing: {cases} PostgreSQL literal round trips and second-statement rejections, nested comments, mixed-case controls and malformed boundaries");
    }
}
