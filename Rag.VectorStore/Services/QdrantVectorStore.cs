using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.VectorStore.Interfaces;
using Rag.VectorStore.Models;

namespace Rag.VectorStore.Services;

public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;

    private const string CollectionName = "document_chunks_2";
    private const ulong VectorSize = 384;

    public QdrantVectorStore(string host = "localhost")
    {
        _client = new QdrantClient(host);
    }

    public async Task InitializeAsync()
    {
        var collections = await _client.ListCollectionsAsync();

        if (collections.Contains(CollectionName))
        {
            await _client.DeleteCollectionAsync(CollectionName);
        }

        await _client.CreateCollectionAsync(
            collectionName: CollectionName,
            vectorsConfig: new VectorParams
            {
                Size = VectorSize,
                Distance = Distance.Cosine
            });
    }

    public async Task UpsertAsync(
        ulong id,
        float[] vector,
        string text,
        string parentText,
        string documentName, int chunkNumber, string documentId)
    {
        var point = new PointStruct
        {
            Id = id,
            Vectors = vector,
            Payload =
            {
                ["documentId"] = documentId,
                ["chunkNumber"] = chunkNumber,
                ["text"] = text,
                ["parentText"] = parentText,
                ["documentName"] = documentName
            }
        };

        await _client.UpsertAsync(
            CollectionName,
            new[] { point });
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
    float[] queryVector,
    int limit = 5)
    {
        // Step 1: Standard Nearest Neighbor (Vector Search) on child chunks
        int fetchLimit = limit * 2;
        var primaryResults = await _client.QueryAsync(
            collectionName: CollectionName,
            query: queryVector,
            limit: (ulong)fetchLimit
        );

        var allSearchResults = new List<VectorSearchResult>();

        foreach (var result in primaryResults)
        {
            var docId = result.Payload.TryGetValue("documentId", out var dId) ? dId.StringValue : string.Empty;
            var chunkNum = result.Payload.TryGetValue("chunkNumber", out var cNum) ? (int)cNum.IntegerValue : 0;
            var score = result.Score;

            // Map primary match
            allSearchResults.Add(MapToResult(result));

            // Step 2: Fetch Immediate Sequential Neighbors (chunkNumber - 1 and chunkNumber + 1)
            // This ensures boundary continuity for the child chunks.
            if (!string.IsNullOrEmpty(docId) && chunkNum > 0)
            {
                var neighborChunks = await FetchNeighborChunksAsync(docId, chunkNum, score);
                allSearchResults.AddRange(neighborChunks);
            }
        }

        // Step 3: Deduplicate by ParentText (or Document + Parent context) and take top limit
        var uniqueResults = allSearchResults
            .OrderByDescending(x => x.Score)
            .GroupBy(x => $"{x.DocumentId}_{x.ChunkNumber}")
            .Select(g => g.First())
            .Take(limit)
            .ToList();

        return uniqueResults;
    }

    private async Task<List<VectorSearchResult>> FetchNeighborChunksAsync(string documentId, int currentChunkNum, float baseScore)
    {
        // Define neighbor range (e.g., previous and next chunk)
        int targetPrev = currentChunkNum - 1;
        int targetNext = currentChunkNum + 1;

        // Use Qdrant filter to find adjacent chunks belonging to the same document
        var filter = new Filter
        {
            Must =
        {
            new Condition
            {
                Field = new FieldCondition
                {
                    Key = "documentId",
                    Match = new Match { Keyword = documentId }
                }
            },
            new Qdrant.Client.Grpc.Condition
            {
                Field = new FieldCondition
                {
                    Key = "chunkNumber",
                    Range = new Qdrant.Client.Grpc.Range
                    {
                        Gte = targetPrev,
                        Lte = targetNext
                    }
                }
            }
        }
        };

        var neighborResults = await _client.QueryAsync(
            collectionName: CollectionName,
            filter: filter,
            limit: 3
        );

        // Give neighbors a slightly discounted score so primary vector matches take precedence
        return neighborResults.Select(n => {
            var res = MapToResult(n);
            res.Score = baseScore * 0.95f; // Slight penalty for neighbors
            return res;
        }).ToList();
    }

    private VectorSearchResult MapToResult(ScoredPoint point)
    {
        return new VectorSearchResult
        {
            Id = point.Id.Num,
            Score = point.Score,
            Text = point.Payload.TryGetValue("text", out var t) ? t.StringValue : string.Empty,
            ParentText = point.Payload.TryGetValue("parentText", out var pt) ? pt.StringValue : string.Empty,
            DocumentName = point.Payload.TryGetValue("documentName", out var dn) ? dn.StringValue : string.Empty,
            DocumentId = point.Payload.TryGetValue("documentId", out var di) ? di.StringValue : string.Empty,
            ChunkNumber = point.Payload.TryGetValue("chunkNumber", out var cn) ? (int)cn.IntegerValue : 0
        };
    }
}