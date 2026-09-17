using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Models;

namespace Rag.DocumentProcessing.Parsers;

public class UniversalDocumentParser : IDocumentParser
{
    private readonly Dictionary<string, IDocumentParser> _parsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDocumentParser _fallbackParser;

    public UniversalDocumentParser()
    {
        var docxParser = new DocxDocumentParser();
        var excelParser = new ExcelDocumentParser();
        var sqlParser = new SqlDocumentParser();
        //var csvParser = new CsvDocumentParser();
        //var jsonParser = new JsonDocumentParser();
        var textParser = new TextDocumentParser();
        var pdfParser = new PdfDocumentParser();

        _fallbackParser = textParser;

        // Register formats
        _parsers[".docx"] = docxParser;
        _parsers[".xlsx"] = excelParser;
        _parsers[".xls"] = excelParser;
        _parsers[".sql"] = sqlParser;
        //_parsers[".csv"] = csvParser;
        //_parsers[".tsv"] = csvParser;
        //_parsers[".json"] = jsonParser;
        _parsers[".txt"] = textParser;
        _parsers[".md"] = textParser;
        _parsers[".log"] = textParser;
        _parsers[".pdf"] = pdfParser;
    }

    public Task<string> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return Task.FromResult(string.Empty);
        }

        var extension = Path.GetExtension(filePath);

        if (_parsers.TryGetValue(extension, out var parser))
        {
            return parser.ParseAsync(filePath, cancellationToken);
        }

        // Fallback for any other text format
        return _fallbackParser.ParseAsync(filePath, cancellationToken);
    }

    /// <summary>
    /// Send 'childText' to Ollama for embedding
    /// Store in Qdrant with payload pointing to its Parent
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="chunkSize"></param>
    /// <param name="childChunkSize"></param>
    /// <param name="overlap"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<List<string>> ParseAndChunkAsync(
       string filePath,
       int chunkSize = DocumentConstants.ChunkSize,
       int childChunkSize = DocumentConstants.ChildChunkSize,
       int overlap = DocumentConstants.Overlap,
       CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return Task.FromResult(new List<string>());
        }

        var extension = Path.GetExtension(filePath);

        if (_parsers.TryGetValue(extension, out var parser))
        {
            return parser.ParseAndChunkAsync(filePath, chunkSize, childChunkSize, overlap, cancellationToken);
        }

        // Fallback for any other text format
        return _fallbackParser.ParseAndChunkAsync(filePath, chunkSize, childChunkSize, overlap, cancellationToken);
    }
}