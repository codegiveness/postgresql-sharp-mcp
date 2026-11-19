// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace PostgreSqlMcp.Core.Tests;

public sealed class SqlParametersTests
{
    private static JsonElement[] Values(string json) => JsonSerializer.Deserialize<JsonElement[]>(json)!;

    private static ToolException Rejected(string json)
    {
        ToolException ex = Assert.Throws<ToolException>(() => SqlParameters.Normalize(Values(json)));
        Assert.Equal("invalid_parameters", ex.Code);
        return ex;
    }

    [Fact]
    public void No_values_means_no_parameters()
    {
        Assert.Null(SqlParameters.Normalize(null));
        Assert.Null(SqlParameters.Normalize([]));
    }

    [Fact]
    public void Scalars_become_their_text_form_and_null_stays_null()
    {
        IReadOnlyList<string?>? texts = SqlParameters.Normalize(Values("""["text", 42, -7.50, 1e3, true, false, null, "", "ünï"]"""));

        Assert.Equal(["text", "42", "-7.50", "1e3", "true", "false", null, "", "ünï"], texts);
    }

    [Fact]
    public void An_undefined_element_is_sql_null() =>
        Assert.Equal([null], SqlParameters.Normalize([default]));

    [Theory]
    [InlineData("12345678901234567890123", "12345678901234567890123")]
    [InlineData("0.1000", "0.1000")]
    [InlineData("-0", "-0")]
    [InlineData("1E+2", "1E+2")]
    public void Numbers_keep_their_exact_source_text_so_no_precision_is_lost(string number, string expected) =>
        Assert.Equal([expected], SqlParameters.Normalize(Values($"[{number}]")));

    [Theory]
    [InlineData("""[{"a":1}]""", 1)]
    [InlineData("""[1, [1,2]]""", 2)]
    [InlineData("""["x", null, {}]""", 3)]
    public void Objects_and_arrays_are_refused_with_a_jsonb_cast_hint(string json, int position)
    {
        ToolException ex = Rejected(json);

        Assert.Equal($"Parameter ${position} must be a string, number, boolean or null. " +
            $"Pass JSON documents and arrays as a string and cast, for example ${position}::jsonb.", ex.Message);
    }

    [Fact]
    public void At_most_256_parameters_are_accepted()
    {
        static string Json(int count) => JsonSerializer.Serialize(Enumerable.Range(0, count));

        Assert.Equal(256, SqlParameters.Normalize(Values(Json(256)))!.Count);
        Assert.Equal("Supply at most 256 parameters.", Rejected(Json(257)).Message);
    }

    [Fact]
    public void Total_size_is_limited_to_131072_utf8_bytes_across_all_values()
    {
        string half = new('a', 65536);

        Assert.Equal(2, SqlParameters.Normalize([JsonSerializer.SerializeToElement(half), JsonSerializer.SerializeToElement(half)])!.Count);
        Assert.Equal("Parameters must total at most 131072 UTF-8 bytes.",
            Assert.Throws<ToolException>(() => SqlParameters.Normalize(
                [JsonSerializer.SerializeToElement(half), JsonSerializer.SerializeToElement(half), JsonSerializer.SerializeToElement("b")])).Message);
    }

    [Fact]
    public void Size_counts_bytes_not_characters()
    {
        // 65537 two-byte characters are 131074 bytes although they are only 65537 characters long.
        JsonElement[] tooBig = [JsonSerializer.SerializeToElement(new string('é', 65537))];

        Assert.Equal("Parameters must total at most 131072 UTF-8 bytes.", Assert.Throws<ToolException>(() => SqlParameters.Normalize(tooBig)).Message);
        Assert.NotNull(SqlParameters.Normalize([JsonSerializer.SerializeToElement(new string('é', 65536))]));
    }

    [Fact]
    public void Nulls_and_other_scalars_have_their_own_small_cost()
    {
        JsonElement[] values = [.. Enumerable.Repeat(JsonSerializer.SerializeToElement((string?)null), 256)];

        Assert.All(SqlParameters.Normalize(values)!, Assert.Null);
    }

    [Fact]
    public void NUL_characters_are_refused_and_the_message_names_the_parameter()
    {
        ToolException ex = Rejected("""["ok", "a\u0000b"]""");

        Assert.Equal("Parameter $2 must not contain NUL characters.", ex.Message);
    }

    [Fact]
    public void Rejections_do_not_echo_values()
    {
        ToolException ex = Rejected("""["synthetic-secret-value", {"password":"synthetic-secret-value"}]""");

        Assert.DoesNotContain("synthetic-secret-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_adds_unnamed_untyped_text_parameters_and_maps_null_to_database_null()
    {
        using var command = new NpgsqlCommand();

        SqlParameters.Bind(command.Parameters, ["1", null, "x'; DROP TABLE t; --"]);

        Assert.Equal(3, command.Parameters.Count);
        Assert.All(command.Parameters.Cast<NpgsqlParameter>(), parameter =>
        {
            Assert.Equal(NpgsqlDbType.Unknown, parameter.NpgsqlDbType);
            Assert.Equal(string.Empty, parameter.ParameterName);
        });
        Assert.Equal("1", command.Parameters[0].Value);
        Assert.Same(DBNull.Value, command.Parameters[1].Value);
        Assert.Equal("x'; DROP TABLE t; --", command.Parameters[2].Value);
    }
}
