// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

/// <summary>
/// The .NET tool is RID-specific and self-contained: <c>dotnet pack</c> writes one pointer package, which only lists
/// the per-RID packages, and one package per runtime identifier. A RID package is named <c>&lt;id&gt;.&lt;rid&gt;</c>
/// and must carry the pointer's exact version. The runtime identifiers are the ones npm already ships.
/// </summary>
internal static class NuGetTool
{
    public static IEnumerable<string> Rids => NpmPlatform.All.Select(platform => platform.Rid);

    /// <summary>Package ids in publication order: every RID package first, the pointer last, so the pointer never names a missing package.</summary>
    public static IEnumerable<string> PackageIds(string packageId) => Rids.Select(rid => packageId + "." + rid).Append(packageId);

    /// <summary>The .nupkg file name <c>dotnet pack</c> gives each package of this version, in publication order.</summary>
    public static IEnumerable<string> FileNames(string packageId, string version) => PackageIds(packageId).Select(id => id + "." + version + ".nupkg");
}
