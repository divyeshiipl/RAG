using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Models;

namespace Rag.DocumentProcessing.Interfaces;

public interface IChunkingService
{
    IReadOnlyList<DocumentChunk> CreateChunks(
         DocumentInfo documentInfo,
         string content,
         int maxChunkSize);

    public IReadOnlyList<DocumentChunk> CreateChildChunks(
       DocumentInfo documentInfo,
       List<string> content,
       int childChunkSize = DocumentConstants.ChildChunkSize,
       int overlap = DocumentConstants.Overlap);

    IReadOnlyList<DocumentChunk> CreateExcelTableChunks(
        DocumentInfo documentInfo,
        IReadOnlyList<ExcelTableBlock> tables,
        int maxChunkWords = DocumentConstants.ChildChunkSize);

}