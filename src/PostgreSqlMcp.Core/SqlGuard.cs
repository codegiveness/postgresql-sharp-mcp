namespace PostgreSqlMcp.Core;

// This is a statement-boundary lexer, not a SQL authorization parser. PostgreSQL
// read-only transactions and the configured role enforce permissions.
public static class SqlGuard
{
    public static string Validate(string sql) => Validate(sql, out _);

    public static string Validate(string sql, out string firstKeyword)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ToolException("invalid_sql", "SQL is required.");
        if (sql.Length > 131072 || sql.Contains('\0')) throw new ToolException("invalid_sql", "SQL must be <=131072 characters without NUL.");
        int i = 0, terminal = -1;
        string? first = null;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            { i += 2; while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2; int depth = 1;
                while (i < sql.Length && depth > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) throw new ToolException("invalid_sql", "Unterminated SQL comment.");
                continue;
            }
            if (terminal >= 0) throw new ToolException("invalid_sql", "Exactly one SQL statement is allowed; transaction and session control are server-owned.");
            if (c == ';') { terminal = i++; continue; }
            if (c is '\'' or '"')
            {
                bool escape = c == '\'' && i > 0 && (sql[i - 1] is 'e' or 'E') && (i < 2 || !IsIdentifier(sql[i - 2]));
                char quote = c; i++; bool closed = false;
                while (i < sql.Length)
                {
                    if (escape && sql[i] == '\\') { i += 2; continue; }
                    if (sql[i++] != quote) continue;
                    if (i < sql.Length && sql[i] == quote) { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new ToolException("invalid_sql", "Unterminated quoted SQL value.");
                continue;
            }
            if (c == '$' && (i == 0 || !IsIdentifier(sql[i - 1])))
            {
                int end = i + 1;
                if (end < sql.Length && (char.IsLetter(sql[end]) || sql[end] == '_'))
                { end++; while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++; }
                if (end < sql.Length && sql[end] == '$')
                {
                    string tag = sql[i..(end + 1)];
                    int close = sql.IndexOf(tag, end + 1, StringComparison.Ordinal);
                    if (close < 0) throw new ToolException("invalid_sql", "Unterminated dollar-quoted SQL value.");
                    i = close + tag.Length; continue;
                }
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i++; while (i < sql.Length && IsIdentifier(sql[i])) i++;
                first ??= sql[start..i].ToUpperInvariant();
            }
            else i++;
        }
        if (first is null) throw new ToolException("invalid_sql", "No executable SQL statement found.");
        if (first is "BEGIN" or "START" or "COMMIT" or "END" or "ROLLBACK" or "ABORT" or "SAVEPOINT" or "RELEASE" or "PREPARE" or "SET" or "RESET" or "DISCARD" or "COPY" or "DO" or "CALL" or "VACUUM")
            throw new ToolException("invalid_sql", "Transaction/session control, COPY, DO, CALL and VACUUM are not supported. Use an administrative client for these operations.");
        firstKeyword = first;
        return terminal < 0 ? sql : sql[..terminal];
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
