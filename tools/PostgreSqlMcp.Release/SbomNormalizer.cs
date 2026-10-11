// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Makes a generated CycloneDX JSON BOM a pure function of its content and the build time. dotnet-CycloneDX stamps a random
/// <c>serialNumber</c> and the wall-clock <c>metadata.timestamp</c>, and offers no option to fix either.
/// </summary>
internal static class SbomNormalizer
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Sets <c>metadata.timestamp</c> and replaces <c>serialNumber</c> with a UUID (version 8, RFC 9562) taken from the SHA-256 of the
    /// BOM without its serial number, so changed content or a different build time yields a different serial.
    /// </summary>
    public static string Normalize(string json, DateTimeOffset timestamp)
    {
        JsonObject bom = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("SBOM is not a JSON object.");
        JsonObject metadata = bom["metadata"] as JsonObject ?? throw new InvalidOperationException("SBOM has no metadata object.");
        int serial = bom.IndexOf("serialNumber");
        int stamp = metadata.IndexOf("timestamp");
        if (serial < 0 || stamp < 0) throw new InvalidOperationException("SBOM lacks the serialNumber or metadata.timestamp to normalize.");

        bom.RemoveAt(serial);
        metadata.SetAt(stamp, timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(bom.ToJsonString()));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        string hex = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        bom.Insert(serial, "serialNumber", $"urn:uuid:{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}");
        return bom.ToJsonString(Indented) + "\n";
    }
}
