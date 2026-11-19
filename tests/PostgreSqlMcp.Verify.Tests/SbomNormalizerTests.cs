// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;

namespace PostgreSqlMcp.Verify.Tests;

// SbomNormalizer lives in the Release tool and is compiled into the verifier from the same source file.
public sealed partial class SbomNormalizerTests
{
    private static readonly DateTimeOffset BuildTime = new(2026, 7, 4, 10, 30, 15, TimeSpan.Zero);

    private static string Bom(string serial = "urn:uuid:11111111-2222-3333-4444-555555555555", string timestamp = "2026-01-01T00:00:00Z", string component = "Npgsql") => $$$"""
        {"bomFormat":"CycloneDX","specVersion":"1.6","serialNumber":"{{{serial}}}","version":1,
         "metadata":{"timestamp":"{{{timestamp}}}","component":{"name":"app"}},
         "components":[{"name":"{{{component}}}","version":"10.0.3"}]}
        """;

    [GeneratedRegex("^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex UuidVersion8();

    private static JsonElement Parse(string json) => JsonElement.Parse(json);

    [Fact]
    public void Output_does_not_depend_on_the_random_serial_or_the_wall_clock_timestamp() =>
        Assert.Equal(SbomNormalizer.Normalize(Bom(), BuildTime),
            SbomNormalizer.Normalize(Bom("urn:uuid:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "2031-12-31T23:59:59Z"), BuildTime));

    [Fact]
    public void The_serial_is_a_version_8_uuid_urn()
    {
        string serial = Parse(SbomNormalizer.Normalize(Bom(), BuildTime)).GetProperty("serialNumber").GetString()!;

        Assert.Matches(UuidVersion8(), serial);
    }

    [Fact]
    public void The_timestamp_is_the_build_time_in_utc_seconds()
    {
        var offset = new DateTimeOffset(2026, 7, 4, 15, 30, 15, 987, TimeSpan.FromHours(5));

        string timestamp = Parse(SbomNormalizer.Normalize(Bom(), offset)).GetProperty("metadata").GetProperty("timestamp").GetString()!;

        Assert.Equal("2026-07-04T10:30:15Z", timestamp);
    }

    [Fact]
    public void Changed_content_or_a_different_build_time_changes_the_serial()
    {
        static string Serial(string json) => Parse(json).GetProperty("serialNumber").GetString()!;
        string reference = Serial(SbomNormalizer.Normalize(Bom(), BuildTime));

        Assert.NotEqual(reference, Serial(SbomNormalizer.Normalize(Bom(component: "Other"), BuildTime)));
        Assert.NotEqual(reference, Serial(SbomNormalizer.Normalize(Bom(), BuildTime.AddSeconds(1))));
    }

    [Fact]
    public void Everything_else_is_preserved_in_order_and_the_serial_keeps_its_position()
    {
        JsonElement bom = Parse(SbomNormalizer.Normalize(Bom(), BuildTime));

        Assert.Equal(["bomFormat", "specVersion", "serialNumber", "version", "metadata", "components"], bom.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Npgsql", bom.GetProperty("components")[0].GetProperty("name").GetString());
        Assert.Equal("app", bom.GetProperty("metadata").GetProperty("component").GetProperty("name").GetString());
    }

    [Fact]
    public void Output_is_indented_json_with_one_trailing_newline()
    {
        string output = SbomNormalizer.Normalize(Bom(), BuildTime);

        Assert.EndsWith("}\n", output, StringComparison.Ordinal);
        Assert.Contains("\n  \"bomFormat\"", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Normalizing_twice_is_stable()
    {
        string once = SbomNormalizer.Normalize(Bom(), BuildTime);

        Assert.Equal(once, SbomNormalizer.Normalize(once, BuildTime));
    }

    [Theory]
    [InlineData("""[1]""", "SBOM is not a JSON object.")]
    [InlineData("""{"serialNumber":"x"}""", "SBOM has no metadata object.")]
    [InlineData("""{"serialNumber":"x","metadata":{}}""", "SBOM lacks the serialNumber or metadata.timestamp to normalize.")]
    [InlineData("""{"metadata":{"timestamp":"t"}}""", "SBOM lacks the serialNumber or metadata.timestamp to normalize.")]
    public void A_bom_that_cannot_be_normalized_is_an_error_not_a_pass_through(string json, string message) =>
        Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => SbomNormalizer.Normalize(json, BuildTime)).Message);
}
