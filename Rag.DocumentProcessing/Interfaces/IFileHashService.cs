namespace Rag.DocumentProcessing.Interfaces;

public interface IFileHashService
{
    Task<string> CalculateHashAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}