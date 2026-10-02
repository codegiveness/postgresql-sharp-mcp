using System.Text;
using PostgreSqlMcp.Core;
using SharpFuzz;

if (args.Length > 0 && args[0] == "campaign")
    return await FuzzCampaign.RunAsync(args);

Fuzzer.LibFuzzer.Run(data =>
{
    // UTF-8 mirrors JSON SQL transport; UTF-16 also explores individual CLR code units.
    CheckSql(Encoding.UTF8.GetString(data));
    if (data.Length % 2 == 0)
    {
        var characters = new char[data.Length / 2];
        for (int index = 0; index < characters.Length; index++)
            characters[index] = (char)(data[index * 2] | data[index * 2 + 1] << 8);
        CheckSql(new string(characters));
    }
});
return 0;

static void CheckSql(string sql)
{
    string normalized, keyword;
    try { normalized = SqlGuard.Validate(sql, out keyword); }
    catch (ToolException exception) when (exception.Code == "invalid_sql") { return; }

    Require(sql.StartsWith(normalized, StringComparison.Ordinal), "Normalization must retain an exact input prefix.");
    Require(normalized.Length > 0 && normalized.Length <= 131072 && !normalized.Contains('\0'), "Accepted SQL violates the input bounds.");
    Require(SqlGuard.Validate(normalized, out string normalizedKeyword) == normalized && normalizedKeyword == keyword,
        "Normalization must be idempotent and preserve the first keyword.");

    // A newline ends any trailing line comment before adding the statement boundary.
    // Staying below the length limit ensures rejection cannot be merely an oversized input.
    if (normalized.Length <= 131072 - 24)
    {
        if (sql != normalized && sql.Length <= 131072 - 24) Reject(sql + "\n; SELECT 987654321");
        Reject(normalized + "\n; SELECT 987654321");
        Require(SqlGuard.Validate("/* boundary */\n" + normalized, out string prefixedKeyword) == "/* boundary */\n" + normalized
            && prefixedKeyword == keyword, "A closed leading comment must not alter statement classification.");
    }
}

static void Reject(string sql)
{
    try { SqlGuard.Validate(sql); }
    catch (ToolException exception) when (exception.Code == "invalid_sql") { return; }
    throw new InvalidOperationException("Accepted an appended second SQL statement.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
