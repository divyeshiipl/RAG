using Rag.DocumentProcessing.Interfaces;
using System.Security.Cryptography;

namespace Rag.DocumentProcessing.Services;

public class FileHashService : IFileHashService
{
    public async Task<string> CalculateHashAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);

        var hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken);

        return Convert.ToHexString(hash);
    }
}
