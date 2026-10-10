using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace PostgreSqlMcp.Core;

/// <summary>
/// Positional values for caller SQL placeholders ($1..$n). Every value is sent as PostgreSQL type <c>unknown</c>
/// in text form, so the server infers its type from context exactly as for a quoted literal; callers cast
/// ($1::int, $1::jsonb) where the context is ambiguous. Values are never interpolated into SQL text.
/// </summary>
public static class SqlParameters
{
    public const int MaxCount = 256;
    public const int MaxBytes = 131072;

    /// <summary>Converts JSON scalars to their text form; null stays SQL NULL. Objects and arrays are refused.</summary>
    public static IReadOnlyList<string?>? Normalize(JsonElement[]? values)
    {
        if (values is null || values.Length == 0) return null;
        if (values.Length > MaxCount)
            throw new ToolException("invalid_parameters", $"Supply at most {MaxCount} parameters.");
        var texts = new string?[values.Length];
        long bytes = 0;
        for (int i = 0; i < values.Length; i++)
        {
            JsonElement value = values[i];
            texts[i] = value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => throw new ToolException("invalid_parameters",
                    $"Parameter ${i + 1} must be a string, number, boolean or null. Pass JSON documents and arrays as a string and cast, for example ${i + 1}::jsonb.")
            };
            if (texts[i] is { } text)
            {
                if (text.Contains('\0'))
                    throw new ToolException("invalid_parameters", $"Parameter ${i + 1} must not contain NUL characters.");
                bytes += Encoding.UTF8.GetByteCount(text);
            }
            if (bytes > MaxBytes)
                throw new ToolException("invalid_parameters", $"Parameters must total at most {MaxBytes} UTF-8 bytes.");
        }
        return texts;
    }

    /// <summary>Adds the values as unnamed (positional) parameters, so Npgsql sends the SQL text unrewritten.</summary>
    public static void Bind(NpgsqlParameterCollection target, IReadOnlyList<string?> values)
    {
        foreach (string? value in values)
            target.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Unknown, Value = (object?)value ?? DBNull.Value });
    }
}
