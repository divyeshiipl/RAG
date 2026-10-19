using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Rag.DocumentProcessing.Configuration;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Data;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Parsers;
using Rag.DocumentProcessing.Services;

var services = new ServiceCollection();
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

services.AddSingleton<IConfiguration>(configuration);
services.AddOptions<DocumentProcessingOptions>()
    .Bind(configuration.GetSection("RagPaths"));

services.AddSingleton<IDocumentParser, UniversalDocumentParser>();
services.AddSingleton<IExcelTableParser, ExcelDocumentParser>();
services.AddSingleton<IChunkingService, ChunkingService>();
services.AddSingleton<IChunkStorage, JsonChunkStorage>();
services.AddSingleton<IFileHashService, FileHashService>();

var serviceProvider = services.BuildServiceProvider();

var parser = serviceProvider.GetRequiredService<IDocumentParser>();
var excelTableParser = serviceProvider.GetRequiredService<IExcelTableParser>();
var chunkingService = serviceProvider.GetRequiredService<IChunkingService>();
var chunkStorage = serviceProvider.GetRequiredService<IChunkStorage>();
var fileHashService = serviceProvider.GetRequiredService<IFileHashService>();

foreach (var document in DocumentRepository.Documents)
{
    Console.WriteLine(
        $"Checking Document: {document.DocumentName}");

    var filePath = Path.Combine(
        serviceProvider
            .GetRequiredService<IOptions<DocumentProcessingOptions>>()
            .Value.DocumentsRootPath,
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
        existingMetadata.FileHash == currentHash &&
        existingMetadata.ChunkingVersion == DocumentConstants.ChunkingVersion)
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

    Console.WriteLine(
        "Creating chunks...");

    IReadOnlyList<Rag.DocumentProcessing.Models.DocumentChunk> chunks;
    if (Path.GetExtension(filePath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(filePath).Equals(".xls", StringComparison.OrdinalIgnoreCase))
    {
        var tables = await excelTableParser.ParseTablesAsync(filePath);
        chunks = chunkingService.CreateExcelTableChunks(
            document,
            tables,
            DocumentConstants.ChildChunkSize);
    }
    else
    {
        var content = await parser.ParseAndChunkAsync(filePath);
        chunks = chunkingService.CreateChildChunks(
            document,
            content,
            DocumentConstants.ChildChunkSize);
    }

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