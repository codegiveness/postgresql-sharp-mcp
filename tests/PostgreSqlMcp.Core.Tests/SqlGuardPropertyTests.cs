// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
namespace PostgreSqlMcp.Core.Tests;

/// <summary>
/// Properties of the statement-boundary lexer over generated SQL whose quoting is always balanced: comments and trailing
/// terminators never change acceptance, a second statement is always refused, and no input can fail with anything but the
/// documented <c>invalid_sql</c> error. A fixed replay seed keeps CI deterministic; change <see cref="Seed"/> ("seed,gamma",
/// gamma odd) to explore other inputs locally.
/// </summary>
public sealed class SqlGuardPropertyTests
{
    private const int Runs = 200;
    private const int MaxSize = 30;
    private const string Seed = "20261119,7919";

    // Fragments that are inert inside any quoted value: statement separators, comment markers and escapes included.
    private static readonly string[] Inert = ["a", "B", " ", "1", ";", "--", "/*", "*/", "\\", "x;y", "É", "''", "-", "*", "/"];
    private static readonly string[] CommentText = ["a", "b", " ", "1", ";", "'", "\"", "$", "-", ",", "$$"];
    private static readonly string[] ControlKeywords =
        ["BEGIN", "START", "COMMIT", "END", "ROLLBACK", "ABORT", "SAVEPOINT", "RELEASE", "PREPARE", "SET", "RESET", "DISCARD", "COPY", "DO", "CALL", "VACUUM"];

    private static Gen<string> Pieces(string[] alphabet, int max) =>
        from count in Gen.Choose(0, max)
        from parts in Gen.ArrayOf(Gen.Elements(alphabet), count)
        select string.Concat(parts);

    private static readonly Gen<string> SingleQuoted = from body in Pieces(Inert, 4) select $"'{body}'";

    // The double-quote fragment is doubled, which is how a quoted identifier carries a quote.
    private static readonly Gen<string> QuotedIdentifier =
        from body in Pieces([.. Inert.Where(piece => piece != "''"), "\"\""], 4) select $"\"{body}\"";

    private static readonly Gen<string> DollarQuoted =
        from tag in Gen.Elements("", "t", "_a1", "body")
        from body in Pieces(Inert, 4)
        select $"${tag}${body}${tag}$";

    private static readonly Gen<string> Operand = Gen.OneOf(
        SingleQuoted, QuotedIdentifier, DollarQuoted,
        from n in Gen.Choose(-500, 500) select n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Gen.Constant("$1"), Gen.Constant("NULL"));

    private static readonly Gen<string> Statement =
        from a in Operand
        from b in Operand
        from shape in Gen.Choose(0, 5)
        select shape switch
        {
            0 => $"SELECT {a}",
            1 => $"SELECT {a}, {b} FROM t WHERE x = {b}",
            2 => $"WITH c AS (SELECT {a}) SELECT * FROM c",
            3 => $"INSERT INTO t VALUES ({a}, {b})",
            4 => $"UPDATE t SET v = {a} WHERE k = {b}",
            _ => $"EXPLAIN SELECT {a} || {b}"
        };

    private static readonly Gen<string> Comment =
        from kind in Gen.Choose(0, 3)
        from text in Pieces(CommentText, 6)
        from inner in Pieces(CommentText, 3)
        from eol in Gen.Elements("\n", "\r\n", "\r")
        select kind switch
        {
            0 => $"/*{text}*/",
            1 => $"/* {text} /* {inner} */ {text} */",
            2 => $"-- {text}{eol}",
            _ => $"/*{text}/*{inner}/*{text}*/{inner}*/*/"
        };

    private static readonly Gen<string> Comments =
        from count in Gen.Choose(0, 3)
        from parts in Gen.ArrayOf(from c in Comment from space in Gen.Elements("", " ", "\n") select c + space, count)
        select string.Concat(parts);

    private static string KeywordOf(string sql)
    {
        SqlGuard.Validate(sql, out string first);
        return first;
    }

    [Property(MaxTest = Runs, EndSize = MaxSize, Replay = Seed)]
    public Property Comments_around_a_valid_statement_never_change_acceptance() =>
        Prop.ForAll(Arb.From(from lead in Comments
                             from statement in Statement
                             from semicolon in Gen.Elements(false, true)
                             from trail in Comments
                             select (lead, statement, semicolon, trail)), input =>
        {
            (string lead, string statement, bool semicolon, string trail) = input;
            string sql = lead + statement + (semicolon ? ";" : "") + trail;

            string accepted = SqlGuard.Validate(sql, out string first);

            Assert.Equal(KeywordOf(statement), first);
            // The terminator and anything after it is dropped; without one, trailing comments stay.
            Assert.Equal(semicolon ? lead + statement : sql, accepted);
            return true;
        });

    [Property(MaxTest = Runs, EndSize = MaxSize, Replay = Seed)]
    public Property A_second_statement_is_always_rejected() =>
        Prop.ForAll(Arb.From(from lead in Comments
                             from first in Statement
                             from between in Comments
                             from second in Statement
                             from closing in Gen.Elements("", ";")
                             select (lead, first, between, second, closing)), input =>
        {
            (string lead, string first, string between, string second, string closing) = input;
            string sql = lead + first + ";" + between + second + closing;

            ToolException ex = Assert.Throws<ToolException>(() => SqlGuard.Validate(sql));

            Assert.Equal("invalid_sql", ex.Code);
            Assert.StartsWith("Exactly one SQL statement is allowed", ex.Message, StringComparison.Ordinal);
            return true;
        });

    [Property(MaxTest = Runs, EndSize = MaxSize, Replay = Seed)]
    public Property Repeated_terminators_are_rejected() =>
        Prop.ForAll(Arb.From(from statement in Statement from extra in Gen.Choose(1, 3) from gap in Comments select (statement, extra, gap)), input =>
        {
            (string statement, int extra, string gap) = input;
            string sql = statement + ";" + gap + string.Concat(Enumerable.Repeat(";" + gap, extra));

            Assert.Contains("Exactly one SQL statement", Assert.Throws<ToolException>(() => SqlGuard.Validate(sql)).Message, StringComparison.Ordinal);
            return true;
        });

    [Property(MaxTest = Runs, EndSize = MaxSize, Replay = Seed)]
    public Property Control_keywords_are_rejected_in_any_case_behind_comments() =>
        Prop.ForAll(Arb.From(from lead in Comments
                             from keyword in Gen.Elements(ControlKeywords)
                             from flips in Gen.ArrayOf(Gen.Elements(true, false), 16)
                             from rest in Gen.Elements("", " x", " TRANSACTION", " SELECT 1")
                             select (lead, keyword, flips, rest)), input =>
        {
            (string lead, string keyword, bool[] flips, string rest) = input;
            string cased = string.Concat(keyword.Select((c, i) => flips[i] ? char.ToLowerInvariant(c) : c));
            string sql = lead + cased + rest;

            ToolException ex = Assert.Throws<ToolException>(() => SqlGuard.Validate(sql));

            Assert.StartsWith("Transaction/session control, COPY, DO, CALL and VACUUM are not supported.", ex.Message, StringComparison.Ordinal);
            return true;
        });

    private static readonly string[] Tokens =
        ["SELECT", "COMMIT", "'", "\"", "$$", "$t$", "$", ";", "--", "/*", "*/", "\n", "\r", "\\", "E'", "e", " ", "x", "1", "(", ")", "é", "\ud800"];

    private static readonly Gen<string> TokenSoup =
        from count in Gen.Choose(0, 24)
        from parts in Gen.ArrayOf(Gen.Elements(Tokens), count)
        select string.Concat(parts);

    [Property(MaxTest = Runs, EndSize = MaxSize, Replay = Seed)]
    public Property Arbitrary_input_is_accepted_as_a_prefix_or_rejected_with_invalid_sql() =>
        Prop.ForAll(Arb.From(Gen.OneOf(TokenSoup, ArbMap.Default.GeneratorFor<string>(), Gen.Elements("\0", "SELECT 1\0"))), sql =>
        {
            try
            {
                string accepted = SqlGuard.Validate(sql, out string first);
                Assert.False(string.IsNullOrEmpty(first));
                Assert.StartsWith(accepted, sql, StringComparison.Ordinal);
                // Only a single terminator may have been removed.
                Assert.True(accepted.Length == sql.Length || sql[accepted.Length] == ';', "Accepted text must end at the input's end or at its terminator.");
            }
            catch (ToolException ex)
            {
                Assert.Equal("invalid_sql", ex.Code);
                Assert.False(string.IsNullOrEmpty(ex.Message));
            }
            return true;
        });
}
