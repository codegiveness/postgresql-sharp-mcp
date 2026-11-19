// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

namespace PostgreSqlMcp.Verify;

/// <summary>Fails when the application, npm and MCP registry manifests disagree on the version or leave the 0.x line.</summary>
internal static class VersionCheck
{
    public static void Run(string root)
    {
        var problems = ProductVersion.Inconsistencies(root);
        Check.That(problems.Count == 0, "Version check failed:" + string.Concat(problems.Select(problem => "\n- " + problem)));
        Console.WriteLine($"Version {ProductVersion.ReadProject(root)} is consistent across {ProductVersion.ProjectFile}, {ProductVersion.NpmManifest} (including optionalDependencies) and {ProductVersion.ServerManifest}.");
    }
}
