namespace PostgreSqlMcp.Core;

public sealed record ColumnInfo(string Name, string Type);
public sealed record CellClip(int Row, int Column);
public sealed record QueryPage(string Database, ColumnInfo[] Columns, List<object?[]> Rows,
    int? RowsAffected, int Offset, int? NextOffset, bool Truncated,
    string? TruncationReason, List<CellClip> ClippedCells);
