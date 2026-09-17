using Microsoft.Extensions.DependencyInjection;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Data;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Parsers;
using Rag.DocumentProcessing.Services;

var services = new ServiceCollection();

services.AddSingleton<IDocumentParser, UniversalDocumentParser>();
services.AddSingleton<IChunkingService, ChunkingService>();
services.AddSingleton<IChunkStorage, JsonChunkStorage>();
services.AddSingleton<IFileHashService, FileHashService>();

var serviceProvider = services.BuildServiceProvider();

var parser = serviceProvider.GetRequiredService<IDocumentParser>();
var chunkingService = serviceProvider.GetRequiredService<IChunkingService>();
var chunkStorage = serviceProvider.GetRequiredService<IChunkStorage>();
var fileHashService = serviceProvider.GetRequiredService<IFileHashService>();

foreach (var document in DocumentRepository.Documents)
{
    Console.WriteLine(
        $"Checking Document: {document.DocumentName}");

    var filePath = Path.Combine(
        DocumentConstants.DocumentsRootPath,
        document.RelativePath.Replace(
            '/',
            Path.DirectorySeparatorChar));

    if (!File.Exists(filePath))
    {
        Console.WriteLine(
            $"File not found: {filePath}");

        await chunkStorage.DeleteAsync(
            document.DocumentId);

        Console.WriteLine(
            "Existing chunks deleted.");

        Console.WriteLine();

        continue;
    }

    var currentHash =
        await fileHashService.CalculateHashAsync(
            filePath);

    var existingMetadata =
        await chunkStorage.GetMetadataAsync(
            document.DocumentId);

    if (existingMetadata != null &&
        existingMetadata.FileHash == currentHash)
    {
        Console.WriteLine(
            "Document unchanged. Skipping.");

        Console.WriteLine();

        continue;
    }

    if (existingMetadata == null)
    {
        Console.WriteLine(
            "New document. Processing...");
    }
    else
    {
        Console.WriteLine(
            "Document modified.");

        Console.WriteLine(
            "Deleting old chunks...");

        await chunkStorage.DeleteAsync(
            document.DocumentId);
    }

    Console.WriteLine(
        "Parsing document...");

    var content =
        await parser.ParseAndChunkAsync(filePath);

    Console.WriteLine(
        "Creating chunks...");

    var chunks =
        chunkingService.CreateChildChunks(
            document,
            content,
            DocumentConstants.ChildChunkSize);

    await chunkStorage.SaveAsync(
        document,
        currentHash,
        chunks);

    Console.WriteLine(
        $"Created {chunks.Count} chunk(s).");

    Console.WriteLine();
}

Console.WriteLine(
    "Checking for deleted documents...");

var repositoryDocumentIds =
    DocumentRepository.Documents
        .Select(x => x.DocumentId)
        .ToHashSet();

var processedDocuments =
    await chunkStorage.GetAllMetadataAsync();

foreach (var processedDocument in processedDocuments)
{
    if (!repositoryDocumentIds.Contains(
        processedDocument.DocumentId))
    {
        Console.WriteLine(
            $"Deleted document detected: " +
            $"{processedDocument.DocumentName}");

        await chunkStorage.DeleteAsync(
            processedDocument.DocumentId);

        Console.WriteLine(
            "Chunks deleted.");

        Console.WriteLine();
    }
}

Console.WriteLine("Document processing completed.");

Console.ReadKey();