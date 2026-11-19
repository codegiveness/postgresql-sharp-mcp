// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace PostgreSqlMcp.Core.Tests;

/// <summary>
/// The reply envelope and the rule that provider text never reaches the caller unless PostgreSQL located it inside the
/// caller's own statement. <see cref="ToolReply"/> keeps its limits in static state, so each test configures what it needs;
/// xunit runs the tests of one class one after another.
/// </summary>
public sealed class ToolReplyTests
{
    private const string Secret = "synthetic-secret-value";

    public ToolReplyTests() => Configure(unrestricted: true, maxResultBytes: 65536);

    private static void Configure(bool unrestricted, int maxResultBytes) =>
        ToolReply.Configure(new ServerOptions
        {
            Targets = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()),
            Unrestricted = unrestricted, MaxResultBytes = maxResultBytes
        });

    private static JsonElement Json(CallToolResult result)
    {
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return JsonDocument.Parse(text.Text).RootElement.Clone();
    }

    private static JsonElement ErrorOf(CallToolResult result)
    {
        Assert.True(result.IsError);
        return Json(result).GetProperty("error");
    }

    private static PostgresException Postgres(string message, string sqlState, string? hint = null, int position = 0, string? where = null,
        string? internalQuery = null, string? constraint = null) =>
        new(message, "ERROR", "ERROR", sqlState, detail: Secret, hint: hint, position: position, internalPosition: 0, internalQuery: internalQuery, where: where,
            schemaName: null, tableName: null, columnName: null, dataTypeName: null, constraintName: constraint, file: null, line: null, routine: null);

    private static Task<CallToolResult> Run(Exception failure) => ToolReply.Run("app", () => throw failure);

    [Fact]
    public void Success_uses_snake_case_and_omits_null_members()
    {
        var page = new QueryPage("app", [new ColumnInfo("id", "int4")], [[1, "a", null]], null, 0, null, false, null, []);

        CallToolResult result = ToolReply.Success(page, "app");

        Assert.False(result.IsError);
        JsonElement json = Json(result);
        Assert.Equal("app", json.GetProperty("database").GetString());
        Assert.Equal("id", json.GetProperty("columns")[0].GetProperty("name").GetString());
        Assert.Equal(3, json.GetProperty("rows")[0].GetArrayLength());
        Assert.Equal(0, json.GetProperty("offset").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.False(json.TryGetProperty("rows_affected", out _));
        Assert.False(json.TryGetProperty("next_offset", out _));
        Assert.False(json.TryGetProperty("truncation_reason", out _));
        Assert.Equal(JsonValueKind.Array, json.GetProperty("clipped_cells").ValueKind);
    }

    [Fact]
    public void Paging_and_truncation_fields_are_reported_when_set()
    {
        var page = new QueryPage("app", [], [], 5, 100, 200, true, "max_rows", [new CellClip(0, 1)]);

        JsonElement json = Json(ToolReply.Success(page, "app"));

        Assert.Equal(5, json.GetProperty("rows_affected").GetInt32());
        Assert.Equal(100, json.GetProperty("offset").GetInt32());
        Assert.Equal(200, json.GetProperty("next_offset").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal("max_rows", json.GetProperty("truncation_reason").GetString());
        Assert.Equal(1, json.GetProperty("clipped_cells")[0].GetProperty("column").GetInt32());
    }

    [Fact]
    public void Success_carries_the_same_json_as_text_and_structured_content()
    {
        CallToolResult result = ToolReply.Success(new { value = 1 }, "app");

        Assert.Equal("""{"value":1}""", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.NotNull(result.StructuredContent);
    }

    [Fact]
    public void Non_finite_numbers_are_written_as_named_literals()
    {
        JsonElement json = Json(ToolReply.Success(new { v = double.NaN, w = double.PositiveInfinity }));

        Assert.Equal("NaN", json.GetProperty("v").GetString());
        Assert.Equal("Infinity", json.GetProperty("w").GetString());
    }

    [Fact]
    public void A_result_over_the_byte_budget_becomes_a_result_too_large_error()
    {
        Configure(unrestricted: true, maxResultBytes: 4096);
        // {"v":"..."} adds 8 bytes around the text, so 4088 characters fill the budget exactly.
        CallToolResult fits = ToolReply.Success(new { v = new string('x', 4088) }, "app");
        CallToolResult over = ToolReply.Success(new { v = new string('x', 4089) }, "app");

        Assert.False(fits.IsError);
        JsonElement error = ErrorOf(over);
        Assert.Equal("result_too_large", error.GetProperty("code").GetString());
        Assert.Equal("app", Json(over).GetProperty("database").GetString());
    }

    [Fact]
    public void The_budget_counts_utf8_bytes_not_characters()
    {
        Configure(unrestricted: true, maxResultBytes: 4096);

        CallToolResult over = ToolReply.Success(new { v = new string('é', 2045) }, "app");

        Assert.True(over.IsError);
    }

    [Fact]
    public void Error_has_code_message_and_sql_state()
    {
        JsonElement json = Json(ToolReply.Error("app", "postgresql_error", "boom", "23505"));

        Assert.Equal("app", json.GetProperty("database").GetString());
        JsonElement error = json.GetProperty("error");
        Assert.Equal("postgresql_error", error.GetProperty("code").GetString());
        Assert.Equal("boom", error.GetProperty("message").GetString());
        Assert.Equal("23505", error.GetProperty("sql_state").GetString());
        Assert.False(error.TryGetProperty("position", out _));
        Assert.False(error.TryGetProperty("server_message", out _));
    }

    [Fact]
    public void Error_text_is_bounded()
    {
        JsonElement json = Json(ToolReply.Error(new string('d', 200), "x", new string('m', 800)));

        Assert.Equal(new string('d', 128) + "… [truncated]", json.GetProperty("database").GetString());
        Assert.Equal(new string('m', 768) + "… [truncated]", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Run_wraps_a_successful_action()
    {
        CallToolResult result = await ToolReply.Run("app", () => Task.FromResult<object>(new { ok = true }));

        Assert.False(result.IsError);
        Assert.True(Json(result).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_tool_exception_keeps_its_code_and_message()
    {
        JsonElement error = ErrorOf(await Run(new ToolException("invalid_sql", "SQL is required.")));

        Assert.Equal("invalid_sql", error.GetProperty("code").GetString());
        Assert.Equal("SQL is required.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Cancellation_is_propagated_not_reported()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(new OperationCanceledException()));
        await Assert.ThrowsAsync<TaskCanceledException>(() => Run(new TaskCanceledException()));
    }

    public static TheoryData<Exception, string> Transport => new()
    {
        { new NpgsqlException("Exception while reading from stream", new TimeoutException()), "timeout" },
        { new TimeoutException($"timed out {Secret}"), "timeout" },
        { new NpgsqlException($"Failed connecting to host=h password={Secret}"), "connection_error" },
        { new SocketException(), "connection_error" },
        { new InvalidOperationException($"bug {Secret}"), "internal_error" },
        { new NullReferenceException(Secret), "internal_error" }
    };

    [Theory]
    [MemberData(nameof(Transport))]
    public async Task Provider_and_unexpected_failures_get_fixed_messages(Exception failure, string code)
    {
        CallToolResult result = await Run(failure);

        JsonElement error = ErrorOf(result);
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Contains("fallback was used.", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Json(result).GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("23505", "postgresql_error", "The operation violates a unique constraint.")]
    [InlineData("23503", "postgresql_error", "The operation violates a foreign key constraint.")]
    [InlineData("23502", "postgresql_error", "The operation violates a NOT NULL constraint.")]
    [InlineData("42P01", "postgresql_error", "A referenced relation does not exist or is not visible in the current search path.")]
    [InlineData("42501", "postgresql_error", "The configured PostgreSQL role does not have permission for this operation.")]
    [InlineData("28P01", "postgresql_error", "PostgreSQL authentication failed. Check the configured credentials and authentication policy.")]
    [InlineData("3D000", "postgresql_error", "The configured PostgreSQL database does not exist.")]
    [InlineData("40P01", "postgresql_error", "PostgreSQL detected a deadlock and aborted the transaction.")]
    [InlineData("22012", "postgresql_error", "PostgreSQL rejected division by zero.")]
    [InlineData("57014", "timeout", "PostgreSQL canceled the operation (statement timeout or cancellation).")]
    public async Task Known_sql_states_map_to_fixed_messages(string sqlState, string code, string message)
    {
        JsonElement error = ErrorOf(await Run(Postgres($"raw server text {Secret}", sqlState)));

        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(sqlState, error.GetProperty("sql_state").GetString());
        Assert.Equal(message, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_rejected_write_message_depends_on_the_access_mode()
    {
        Configure(unrestricted: true, maxResultBytes: 65536);
        string unrestricted = (await ErrorMessage("25006"));
        Configure(unrestricted: false, maxResultBytes: 65536);
        string restricted = await ErrorMessage("25006");

        Assert.Contains("retry execute_sql with read_only=false", unrestricted, StringComparison.Ordinal);
        Assert.Contains("restricted access mode and refuses writes", restricted, StringComparison.Ordinal);
        Assert.DoesNotContain("read_only=false", restricted, StringComparison.Ordinal);

        static async Task<string> ErrorMessage(string state) => ErrorOf(await Run(Postgres("x", state))).GetProperty("message").GetString()!;
    }

    [Fact]
    public async Task An_unmapped_sql_state_withholds_the_server_text()
    {
        CallToolResult result = await Run(Postgres($"row value {Secret}", "XX000", hint: $"hint {Secret}"));

        JsonElement error = ErrorOf(result);
        Assert.Contains("withheld because they may contain sensitive values", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Json(result).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Constraint_violation_text_is_withheld_but_the_constraint_name_is_reported()
    {
        CallToolResult result = await Run(Postgres($"duplicate key value (email)=({Secret})", "23505", hint: Secret, constraint: "users_email_key"));

        JsonElement error = ErrorOf(result);
        Assert.Equal("users_email_key", error.GetProperty("constraint").GetString());
        Assert.False(error.TryGetProperty("server_message", out _));
        Assert.DoesNotContain(Secret, Json(result).GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("routine", null)]
    [InlineData(null, "SELECT internal")]
    public async Task A_constraint_raised_inside_a_routine_or_internal_query_is_not_reported(string? where, string? internalQuery)
    {
        JsonElement error = ErrorOf(await Run(Postgres("x", "23505", where: where, internalQuery: internalQuery, constraint: "users_email_key")));

        Assert.False(error.TryGetProperty("constraint", out _));
    }

    [Fact]
    public async Task Syntax_errors_located_in_the_callers_statement_forward_message_hint_and_position()
    {
        const string statement = "SELECT 1 FORM t";
        PostgresException failure = Postgres("syntax error at or near \"FORM\"", "42601", hint: "Did you mean FROM?", position: 12);

        JsonElement error = ErrorOf(await ToolReply.Run("app", () =>
        {
            ToolReply.MarkStatementPosition(failure, 0, statement);
            throw failure;
        }));

        Assert.Equal(12, error.GetProperty("position").GetInt32());
        Assert.Equal("syntax error at or near \"FORM\"", error.GetProperty("server_message").GetString());
        Assert.Equal("Did you mean FROM?", error.GetProperty("server_hint").GetString());
        Assert.Equal("PostgreSQL rejected the SQL syntax. Check the statement using the returned SQLSTATE.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_class_42_error_without_a_statement_position_is_withheld()
    {
        CallToolResult result = await Run(Postgres($"role \"{Secret}\" does not exist", "42704", position: 0));

        JsonElement error = ErrorOf(result);
        Assert.False(error.TryGetProperty("server_message", out _));
        Assert.DoesNotContain(Secret, Json(result).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Privilege_errors_forward_their_text_without_a_position()
    {
        JsonElement error = ErrorOf(await Run(Postgres("permission denied for table accounts", "42501")));

        Assert.Equal("permission denied for table accounts", error.GetProperty("server_message").GetString());
        Assert.False(error.TryGetProperty("position", out _));
    }

    [Fact]
    public async Task Text_from_a_routine_context_is_withheld_even_with_a_position()
    {
        PostgresException failure = Postgres($"column \"{Secret}\" does not exist", "42703", position: 8, where: "PL/pgSQL function f() line 3");

        JsonElement error = ErrorOf(await ToolReply.Run("app", () =>
        {
            ToolReply.MarkStatementPosition(failure, 0, "SELECT f()");
            throw failure;
        }));

        Assert.Equal(8, error.GetProperty("position").GetInt32());
        Assert.False(error.TryGetProperty("server_message", out _));
    }

    [Theory]
    [InlineData(15, 5, 10, true)]
    [InlineData(5, 5, 10, false)]
    [InlineData(6, 5, 10, true)]
    [InlineData(16, 5, 10, false)]
    [InlineData(0, 0, 10, false)]
    public async Task The_statement_position_is_relative_to_the_callers_statement_and_bounded_by_its_length(int serverPosition, int prefix, int statementLength, bool recorded)
    {
        string statement = new('a', statementLength);
        PostgresException failure = Postgres("syntax error", "42601", position: serverPosition);

        JsonElement error = ErrorOf(await ToolReply.Run("app", () =>
        {
            Assert.False(ToolReply.MarkStatementPosition(failure, prefix, statement));
            throw failure;
        }));

        Assert.Equal(recorded, error.TryGetProperty("position", out JsonElement position));
        if (recorded) Assert.Equal(serverPosition - prefix, position.GetInt32());
    }

    [Fact]
    public async Task Positions_count_code_points_not_utf16_units()
    {
        // One emoji is a single code point (position 1) but two UTF-16 units.
        PostgresException inside = Postgres("syntax error", "42601", position: 1);
        PostgresException beyond = Postgres("syntax error", "42601", position: 2);

        JsonElement first = ErrorOf(await ToolReply.Run("app", () => { ToolReply.MarkStatementPosition(inside, 0, "😀"); throw inside; }));
        JsonElement second = ErrorOf(await ToolReply.Run("app", () => { ToolReply.MarkStatementPosition(beyond, 0, "😀"); throw beyond; }));

        Assert.True(first.TryGetProperty("position", out _));
        Assert.False(second.TryGetProperty("position", out _));
    }

    [Fact]
    public async Task Forwarded_server_text_is_bounded()
    {
        PostgresException failure = Postgres(new string('m', 600), "42601", hint: new string('h', 600), position: 1);

        JsonElement error = ErrorOf(await ToolReply.Run("app", () => { ToolReply.MarkStatementPosition(failure, 0, "SELECT 1"); throw failure; }));

        Assert.Equal(new string('m', 512) + "… [truncated]", error.GetProperty("server_message").GetString());
        Assert.Equal(new string('h', 512) + "… [truncated]", error.GetProperty("server_hint").GetString());
    }
}
