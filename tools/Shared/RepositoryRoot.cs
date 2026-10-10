internal static class RepositoryRoot
{
    /// <summary>The nearest ancestor of the current directory (inclusive) containing the solution file, or null outside the repository.</summary>
    public static string? Find()
    {
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "postgresql-sharp-mcp.slnx"))) return directory.FullName;
        return null;
    }
}
