namespace Rag.DocumentProcessing.Models;

public sealed class ExcelTableBlock
{
    public string SheetName { get; init; } = string.Empty;
    public string TableContext { get; init; } = string.Empty;
    public int TableIndex { get; init; }
    public IReadOnlyList<string> Headers { get; init; } = [];
    public IReadOnlyList<ExcelTableRow> Rows { get; init; } = [];
}

public sealed class ExcelTableRow
{
    public int RowIndex { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<string> Cells { get; init; } = [];
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Identifiers { get; init; } = [];
}
