using Microsoft.Extensions.DependencyInjection;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Parsers;
using Rag.DocumentProcessing.Services;

namespace Rag.DocumentProcessing.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentProcessing(
        this IServiceCollection services)
    {
        services.AddSingleton<IDocumentParser, DocxDocumentParser>();
        services.AddSingleton<IChunkingService, ChunkingService>();
        services.AddSingleton<IChunkStorage, JsonChunkStorage>();

        return services;
    }
}