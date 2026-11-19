// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

namespace PostgreSqlMcp.Core.Tests;

public sealed class SqlGuardTests
{
    private const string TooManyStatements = "Exactly one SQL statement is allowed";

    private static ToolException Rejected(string? sql)
    {
        ToolException ex = Assert.Throws<ToolException>(() => SqlGuard.Validate(sql!));
        Assert.Equal("invalid_sql", ex.Code);
        return ex;
    }

    [Theory]
    [InlineData("SELECT 1", "SELECT 1", "SELECT")]
    [InlineData("select 1", "select 1", "SELECT")]
    [InlineData("  \n\tWITH x AS (SELECT 1) SELECT * FROM x", "  \n\tWITH x AS (SELECT 1) SELECT * FROM x", "WITH")]
    [InlineData("(SELECT 1)", "(SELECT 1)", "SELECT")]
    [InlineData("INSERT INTO t VALUES (1)", "INSERT INTO t VALUES (1)", "INSERT")]
    [InlineData("explain analyze select 1", "explain analyze select 1", "EXPLAIN")]
    [InlineData("_private_fn()", "_private_fn()", "_PRIVATE_FN")]
    public void Single_statement_is_accepted_and_first_keyword_is_upper_cased(string sql, string expected, string keyword)
    {
        Assert.Equal(expected, SqlGuard.Validate(sql, out string first));
        Assert.Equal(keyword, first);
        Assert.Equal(expected, SqlGuard.Validate(sql));
    }

    [Theory]
    [InlineData("SELECT 1;", "SELECT 1")]
    [InlineData("SELECT 1 ;  ", "SELECT 1 ")]
    [InlineData("SELECT 1; -- trailing\n", "SELECT 1")]
    [InlineData("SELECT 1; /* trailing */", "SELECT 1")]
    [InlineData("SELECT 1;\r\n-- a\r\n/* b /* nested */ */\r\n", "SELECT 1")]
    public void One_trailing_terminator_is_stripped_and_only_comments_may_follow(string sql, string expected) =>
        Assert.Equal(expected, SqlGuard.Validate(sql));

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1;;")]
    [InlineData("SELECT 1; ;")]
    [InlineData("SELECT 1; -- c\nSELECT 2")]
    [InlineData("SELECT 1; /* c */ DROP TABLE t")]
    [InlineData("SELECT 1;SELECT 2;")]
    [InlineData("SELECT 1; 2")]
    public void More_than_one_statement_is_rejected(string sql) =>
        Assert.Contains(TooManyStatements, Rejected(sql).Message, StringComparison.Ordinal);

    [Fact]
    public void Transaction_control_after_a_statement_hits_the_single_statement_rule_not_the_keyword_rule() =>
        Assert.Contains(TooManyStatements, Rejected("SELECT 1; COMMIT").Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("SELECT 'a;b'", "SELECT 'a;b'")]
    [InlineData("SELECT 'it''s; fine'", "SELECT 'it''s; fine'")]
    [InlineData("SELECT \"a;b\" FROM t", "SELECT \"a;b\" FROM t")]
    [InlineData("SELECT \"a\"\"; DROP\" FROM t", "SELECT \"a\"\"; DROP\" FROM t")]
    [InlineData("SELECT 'one' ; ", "SELECT 'one' ")]
    [InlineData("SELECT '--'", "SELECT '--'")]
    [InlineData("SELECT '/*'", "SELECT '/*'")]
    [InlineData("SELECT '*/' ; -- ok", "SELECT '*/' ")]
    [InlineData("SELECT $1, $2", "SELECT $1, $2")]
    [InlineData("SELECT a$b FROM t", "SELECT a$b FROM t")]
    public void Semicolons_and_comment_markers_inside_quotes_are_data(string sql, string expected) =>
        Assert.Equal(expected, SqlGuard.Validate(sql));

    [Theory]
    [InlineData("SELECT E'it\\'s; still inside'")]
    [InlineData("SELECT e'a\\\\'")]
    [InlineData("SELECT (E'x\\'; DROP TABLE t; --')")]
    public void Backslash_escapes_apply_only_inside_E_strings(string sql) =>
        Assert.Equal(sql, SqlGuard.Validate(sql));

    [Theory]
    [InlineData("SELECT 'a\\'; DROP TABLE t; --'")]
    [InlineData("SELECT xe'\\'; SELECT 2")]
    public void Backslash_does_not_escape_a_quote_in_standard_strings(string sql) =>
        Assert.Contains(TooManyStatements, Rejected(sql).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("SELECT 'abc")]
    [InlineData("SELECT \"abc")]
    [InlineData("SELECT 'it''s")]
    [InlineData("SELECT E'abc\\'")]
    [InlineData("SELECT 'a' || '")]
    public void Unterminated_quoted_value_is_rejected(string sql) =>
        Assert.Equal("Unterminated quoted SQL value.", Rejected(sql).Message);

    [Theory]
    [InlineData("SELECT $$a;b$$")]
    [InlineData("SELECT $tag$ ; DROP TABLE t; $tag$")]
    [InlineData("SELECT $a$ x $b$ ; $a$")]
    [InlineData("SELECT $_t1$ ' \" -- /* $_t1$")]
    [InlineData("SELECT $$$$")]
    [InlineData("SELECT $q$ $Q$ ; $q$")]
    public void Dollar_quoted_bodies_hide_semicolons_and_other_quotes(string sql) =>
        Assert.Equal(sql, SqlGuard.Validate(sql));

    [Theory]
    [InlineData("SELECT $$ never closed")]
    [InlineData("SELECT $tag$ x $$")]
    [InlineData("SELECT $a$ x $b$")]
    [InlineData("SELECT $tag$ x $TAG$")]
    public void Unterminated_dollar_quote_is_rejected(string sql) =>
        Assert.Equal("Unterminated dollar-quoted SQL value.", Rejected(sql).Message);

    [Theory]
    [InlineData("SELECT $1; SELECT 2")]
    [InlineData("SELECT a$$ ; SELECT 2")]
    [InlineData("SELECT 1 AS x$y$z ; DROP TABLE t")]
    public void A_dollar_inside_an_identifier_or_parameter_is_not_a_dollar_quote(string sql) =>
        Assert.Contains(TooManyStatements, Rejected(sql).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("/* c */ SELECT 1")]
    [InlineData("/* a /* b */ c */ SELECT 1")]
    [InlineData("/* a /* b /* c */ d */ e */ SELECT 1")]
    [InlineData("SELECT 1 /* ; DROP TABLE t */")]
    [InlineData("SELECT /* x */ 1 -- ; DROP TABLE t")]
    [InlineData("-- first\r-- second\nSELECT 1")]
    [InlineData("/**/SELECT 1")]
    [InlineData("SELECT 1 /* ; */ ; /* ; */")]
    public void Comments_are_skipped_including_nested_block_comments(string sql) =>
        Assert.Equal("SELECT", FirstKeyword(sql));

    private static string FirstKeyword(string sql)
    {
        SqlGuard.Validate(sql, out string first);
        return first;
    }

    [Theory]
    [InlineData("/* open")]
    [InlineData("/* a /* b */ SELECT 1")]
    [InlineData("SELECT 1 /* a /* b */")]
    [InlineData("SELECT 1 /*")]
    [InlineData("/*/ SELECT 1")]
    public void Unterminated_block_comment_is_rejected(string sql) =>
        Assert.Equal("Unterminated SQL comment.", Rejected(sql).Message);

    [Fact]
    public void A_line_comment_ends_at_carriage_return_or_line_feed()
    {
        Assert.Equal("-- one\rSELECT 1", SqlGuard.Validate("-- one\rSELECT 1"));
        Assert.Equal("-- one\nSELECT 1", SqlGuard.Validate("-- one\nSELECT 1"));
        Rejected("SELECT 1 -- ; \n; SELECT 2");
    }

    [Theory]
    [InlineData("BEGIN")]
    [InlineData("start transaction")]
    [InlineData("COMMIT")]
    [InlineData("end")]
    [InlineData("rollback to savepoint a")]
    [InlineData("ABORT")]
    [InlineData("savepoint a")]
    [InlineData("release savepoint a")]
    [InlineData("prepare p as select 1")]
    [InlineData("SET search_path = public")]
    [InlineData("reset all")]
    [InlineData("discard all")]
    [InlineData("COPY t TO STDOUT")]
    [InlineData("do $$ begin null; end $$")]
    [InlineData("CALL proc()")]
    [InlineData("vacuum")]
    [InlineData("/* leading */ -- comment\n CoMmIt;")]
    [InlineData("(SET x = 1)")]
    public void Transaction_session_copy_do_call_and_vacuum_are_rejected(string sql)
    {
        ToolException ex = Rejected(sql);
        Assert.StartsWith("Transaction/session control, COPY, DO, CALL and VACUUM are not supported.", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT set_config('x', 'y', false)")]
    [InlineData("WITH s AS (SELECT 1) SELECT * FROM s")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("SELECT 'BEGIN'")]
    [InlineData("SELECT 1 AS \"COMMIT\"")]
    [InlineData("SHOW search_path")]
    [InlineData("VALUES (1)")]
    [InlineData("TABLE t")]
    [InlineData("endpoint_fn()")]
    [InlineData("set_config_like()")]
    public void Only_the_leading_keyword_decides_so_names_and_literals_do_not_trip_the_control_list(string sql) =>
        Assert.Equal(sql, SqlGuard.Validate(sql));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Blank_sql_is_required(string? sql) => Assert.Equal("SQL is required.", Rejected(sql).Message);

    [Theory]
    [InlineData("-- only a comment")]
    [InlineData("/* only */")]
    [InlineData(";")]
    [InlineData("  ;  -- nothing")]
    [InlineData("$$ only a literal $$")]
    [InlineData("'only a literal'")]
    [InlineData("\"quoted\"")]
    [InlineData("1")]
    [InlineData("(1, 2)")]
    public void Input_without_an_identifier_or_keyword_has_no_executable_statement(string sql) =>
        Assert.Equal("No executable SQL statement found.", Rejected(sql).Message);

    [Fact]
    public void Length_limit_is_131072_characters_inclusive()
    {
        string atLimit = "SELECT 1" + new string(' ', 131072 - "SELECT 1".Length);
        Assert.Equal(131072, atLimit.Length);
        Assert.Equal(atLimit, SqlGuard.Validate(atLimit));

        ToolException ex = Rejected(atLimit + " ");
        Assert.Equal("SQL must be <=131072 characters without NUL.", ex.Message);
    }

    [Theory]
    [InlineData("SELECT 1\0")]
    [InlineData("\0SELECT 1")]
    [InlineData("SELECT '\0'")]
    [InlineData("-- \0\nSELECT 1")]
    public void NUL_is_rejected_everywhere_even_inside_literals_and_comments(string sql) =>
        Assert.Equal("SQL must be <=131072 characters without NUL.", Rejected(sql).Message);

    [Fact]
    public void Rejection_messages_never_echo_the_submitted_sql()
    {
        const string secret = "hunter2-synthetic";
        foreach (string sql in new[] { $"SELECT '{secret}", $"SELECT 1; SELECT '{secret}'", $"COMMIT -- {secret}", $"/* {secret}" })
            Assert.DoesNotContain(secret, Rejected(sql).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_ascii_letters_form_identifiers()
    {
        Assert.Equal("ÜBER", FirstKeyword("über(1)"));
        Assert.Equal("SELECT é$1$", SqlGuard.Validate("SELECT é$1$"));
    }
}
