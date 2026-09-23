using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.VectorStore.Interfaces;
using Rag.VectorStore.Models;
using System.Text.RegularExpressions;

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

        await _client.CreatePayloadIndexAsync(
            collectionName: CollectionName,
            fieldName: "text",
            schemaType: PayloadSchemaType.Text,
            indexParams: new PayloadIndexParams
            {
                TextIndexParams = new TextIndexParams
                {
                    Tokenizer = TokenizerType.Word,
                    Lowercase = true,
                    MinTokenLen = 2
                }
            });

        await _client.CreatePayloadIndexAsync(
            collectionName: CollectionName,
            fieldName: "searchText",
            schemaType: PayloadSchemaType.Text,
            indexParams: new PayloadIndexParams
            {
                TextIndexParams = new TextIndexParams
                {
                    Tokenizer = TokenizerType.Word,
                    Lowercase = true,
                    MinTokenLen = 2
                }
            });
    }

    public async Task UpsertAsync(
        ulong id,
        float[] vector,
        string text,
        string parentText,
        string documentName,
        int chunkNumber,
        string documentId,
        string documentType = "",
        string fileType = "",
        string relativePath = "",
        int? pageNumber = null,
        string? sheetName = null,
        string? sqlObjectType = null)
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
                ["documentName"] = documentName,
                ["documentType"] = documentType,
                ["fileType"] = fileType,
                ["relativePath"] = relativePath,
                ["searchText"] = $"{documentName} {relativePath} {text}"
            }
        };

        if (pageNumber.HasValue)
            point.Payload["pageNumber"] = pageNumber.Value;

        if (!string.IsNullOrWhiteSpace(sheetName))
            point.Payload["sheetName"] = sheetName;

        if (!string.IsNullOrWhiteSpace(sqlObjectType))
            point.Payload["sqlObjectType"] = sqlObjectType;

        await _client.UpsertAsync(CollectionName, new[] { point });
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        string queryText,
        int limit = 5)
    {
        // Step 1: Semantic (vector) search
        var semanticResults = await _client.QueryAsync(
            collectionName: CollectionName,
            query: queryVector,
            limit: (ulong)(limit * 3));

        // Step 2: Full-text (keyword) search
        var cleanedQuery = CleanQueryForFullText(queryText);

        var scrollResponse = await _client.ScrollAsync(
            collectionName: CollectionName,
            filter: new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "text",
                            Match = new Qdrant.Client.Grpc.Match { Text = cleanedQuery }
                        }
                    }
                }
            },
            limit: (uint)(limit * 3));

        var textResults = scrollResponse.Result?.ToList() ?? new List<RetrievedPoint>();
        var lexicalResults = await SearchLexicalCandidatesAsync(queryText);
        var queryIdentifiers = ExtractQueryIdentifiers(queryText);

        if (queryIdentifiers.Count > 0)
        {
            var exactLexicalResults = lexicalResults
                .Where(result => ContainsAllIdentifierTokens(
                    MapRetrievedToResult(result),
                    queryIdentifiers))
                .ToList();

            if (exactLexicalResults.Count > 0)
            {
                lexicalResults = exactLexicalResults;
            }

        }

        // Step 3: Reciprocal Rank Fusion
        var combined = new Dictionary<ulong, double>();

        for (int i = 0; i < semanticResults.Count; i++)
        {
            var id = semanticResults[i].Id.Num;
            combined[id] = combined.GetValueOrDefault(id) + 1.0 / (i + 60);
        }

        for (int i = 0; i < textResults.Count; i++)
        {
            var id = textResults[i].Id.Num;
            combined[id] = combined.GetValueOrDefault(id) + 1.0 / (i + 60);
        }

        foreach (var result in lexicalResults)
        {
            combined[result.Id.Num] = combined.GetValueOrDefault(result.Id.Num) + 1.0;
        }

        var candidateResults = semanticResults
            .Select(MapToResult)
            .Concat(textResults.Select(MapRetrievedToResult))
            .Concat(lexicalResults.Select(MapRetrievedToResult))
            .GroupBy(x => x.Id)
            .Select(group => group.First())
            .ToList();

        if (queryIdentifiers.Count > 0)
        {
            var identifierMatches = candidateResults
                .Where(result => ContainsAllIdentifierTokens(result, queryIdentifiers))
                .Select(result => result.Id)
                .ToHashSet();

            if (identifierMatches.Count > 0)
            {
                candidateResults = candidateResults
                    .Where(result =>
                        identifierMatches.Contains(result.Id) ||
                        !ContainsAnyIdentifierTokens(result))
                    .ToList();

                var allowedIds = candidateResults
                    .Select(result => result.Id)
                    .ToHashSet();

                lexicalResults = lexicalResults
                    .Where(result => allowedIds.Contains(result.Id.Num))
                    .ToList();
            }
        }

        foreach (var candidate in candidateResults)
        {
            combined[candidate.Id] = combined.GetValueOrDefault(candidate.Id) +
                                      CalculateLexicalBoost(candidate, queryText);
        }

        var lexicalScores = candidateResults
            .ToDictionary(
                result => result.Id,
                result => CalculateLexicalBoost(result, queryText));

        var topIds = combined
            .OrderByDescending(x => lexicalScores.GetValueOrDefault(x.Key) * 10 + x.Value)
            .Take(limit * 2)
            .Select(x => x.Key)
            .ToHashSet();

        foreach (var lexicalId in lexicalScores
                     .OrderByDescending(x => x.Value)
                     .Take(limit)
                     .Select(x => x.Key))
        {
            topIds.Add(lexicalId);
        }

        // Step 4: Collect matching results
        var allSearchResults = new List<VectorSearchResult>();

        foreach (var result in lexicalResults.Take(limit))
        {
            var mappedResult = MapRetrievedToResult(result);
            mappedResult.Score = 1000 +
                (float)CalculateLexicalBoost(mappedResult, queryText);
            allSearchResults.Add(mappedResult);
        }

        foreach (var result in semanticResults)
        {
            if (!topIds.Contains(result.Id.Num))
                continue;

            var mappedResult = MapToResult(result);
            mappedResult.Score += (float)(CalculateLexicalBoost(mappedResult, queryText) * 10);

            if (allSearchResults.Any(r => r.Id == mappedResult.Id))
            {
                continue;
            }

            allSearchResults.Add(mappedResult);
        }

        foreach (var result in textResults)
        {
            if (!topIds.Contains(result.Id.Num))
                continue;

            if (allSearchResults.Any(r => r.Id == result.Id.Num))
                continue;

            var mappedResult = MapRetrievedToResult(result);
            mappedResult.Score += (float)(CalculateLexicalBoost(mappedResult, queryText) * 10);
            allSearchResults.Add(mappedResult);
        }

        foreach (var result in lexicalResults)
        {
            if (!topIds.Contains(result.Id.Num) ||
                allSearchResults.Any(r => r.Id == result.Id.Num))
            {
                continue;
            }

            var mappedResult = MapRetrievedToResult(result);
            mappedResult.Score += (float)(
                CalculateLexicalBoost(mappedResult, queryText) * 10 + 1);
            allSearchResults.Add(mappedResult);
        }

        // Neighbor chunks can introduce unrelated records for identifier-specific
        // questions, so preserve only the directly matched chunks in that case.
        var neighborResults = new List<VectorSearchResult>();

        if (queryIdentifiers.Count == 0)
        {
            foreach (var result in allSearchResults.ToList())
            {
                if (!string.IsNullOrEmpty(result.DocumentId) && result.ChunkNumber > 0)
                {
                    var neighbors = await FetchNeighborChunksAsync(
                        result.DocumentId, result.ChunkNumber, result.Score);

                    neighborResults.AddRange(neighbors);
                }
            }
        }

        allSearchResults.AddRange(neighborResults);

        // Step 6: Deduplicate and return top results
        var uniqueResults = allSearchResults
            .OrderByDescending(x => x.Score)
            .GroupBy(x => $"{x.DocumentId}_{x.ChunkNumber}")
            .Select(g => g.First())
            .Take(limit)
            .ToList();

        return uniqueResults;
    }

    private static IReadOnlyList<string> ExtractQueryIdentifiers(string query)
    {
        return Regex.Matches(
                query,
                @"\b[A-Za-z][A-Za-z0-9]*(?:[-_][A-Za-z0-9]+)+\b|\b(?:v(?:ersion)?\s*)?\d+(?:\.\d+)+\b",
                RegexOptions.IgnoreCase)
            .Select(match => match.Value)
            .ToList();
    }

    private static bool ContainsAllIdentifierTokens(
        VectorSearchResult result,
        IReadOnlyList<string> identifiers)
    {
        var searchableText = NormalizeForSearch(
            $"{result.DocumentName} {result.RelativePath} {result.Text}");

        return identifiers.All(identifier =>
        {
            var identifierText = NormalizeIdentifier(identifier);
            return searchableText.Contains(
                identifierText,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string NormalizeIdentifier(string identifier)
    {
        var versionMatch = Regex.Match(
            identifier,
            @"\d+(?:\.\d+)+",
            RegexOptions.IgnoreCase);

        return versionMatch.Success
            ? NormalizeForSearch(versionMatch.Value)
            : NormalizeForSearch(identifier);
    }

    private static bool ContainsAnyIdentifierTokens(VectorSearchResult result)
    {
        var searchableText =
            $"{result.DocumentName} {result.RelativePath} {result.Text}";

        return ExtractQueryIdentifiers(searchableText).Count > 0;
    }

    private async Task<IReadOnlyList<RetrievedPoint>> SearchLexicalCandidatesAsync(
        string query)
    {
        var terms = Tokenize(query)
            .Where(term => term.Length >= 2 || term.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (terms.Count == 0)
        {
            return Array.Empty<RetrievedPoint>();
        }

        var response = await _client.ScrollAsync(
            collectionName: CollectionName,
            limit: 10000);

        return (response.Result ?? [])
            .Select(point => new
            {
                Point = point,
                Score = CalculateLexicalPayloadScore(point, terms)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Point)
            .ToList();
    }

    private static int CalculateLexicalPayloadScore(
        RetrievedPoint point,
        IReadOnlyList<string> terms)
    {
        var searchableText = string.Join(
            " ",
            GetPayloadValue(point, "documentName"),
            GetPayloadValue(point, "relativePath"),
            GetPayloadValue(point, "text"));

        var documentTerms = Tokenize(searchableText).ToList();

        return CalculateTokenMatchScore(documentTerms, terms);
    }

    private static string GetPayloadValue(
        RetrievedPoint point,
        string key)
    {
        return point.Payload.TryGetValue(key, out var value)
            ? value.StringValue
            : string.Empty;
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what", "which", "who", "where", "when", "why", "how",
            "are", "is", "the", "a", "an", "of", "for", "in", "on",
            "to", "and", "or", "from", "by", "with", "does", "do",
            "can", "should", "were", "was", "be", "this", "that",
            "all", "any", "name", "give", "me", "please", "tell"
        };

        return Regex.Matches(value.ToLowerInvariant(), @"[a-z]+|[0-9]+")
            .Select(match => match.Value)
            .Where(term => !stopWords.Contains(term))
            .ToList();
    }

    private static int CalculateTokenMatchScore(
        IReadOnlyList<string> documentTerms,
        IReadOnlyList<string> queryTerms)
    {
        var documentSet = documentTerms.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var score = queryTerms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(term => documentSet.Contains(term)
                ? term.Any(char.IsDigit) ? 10 : 1
                : 0);

        for (var length = Math.Min(4, queryTerms.Count); length >= 2; length--)
        {
            for (var start = 0; start <= queryTerms.Count - length; start++)
            {
                var phrase = queryTerms
                    .Skip(start)
                    .Take(length)
                    .ToList();

                for (var documentStart = 0;
                     documentStart <= documentTerms.Count - length;
                     documentStart++)
                {
                    if (phrase.SequenceEqual(
                        documentTerms.Skip(documentStart).Take(length),
                        StringComparer.OrdinalIgnoreCase))
                    {
                        score += length * length;
                        break;
                    }
                }
            }
        }

        return score;
    }

    private static double CalculateLexicalBoost(
        VectorSearchResult result,
        string query)
    {
        var searchableText =
            $"{result.DocumentName} {result.RelativePath} {result.Text}";

        var normalizedQuery = NormalizeForSearch(query);
        var normalizedText = NormalizeForSearch(searchableText);

        if (string.IsNullOrWhiteSpace(normalizedQuery) ||
            string.IsNullOrWhiteSpace(normalizedText))
        {
            return 0;
        }

        var queryTerms = Tokenize(query)
            .Where(term => term.Length >= 2 || term.Any(char.IsDigit))
            .ToList();

        double boost = CalculateTokenMatchScore(
            Tokenize(normalizedText),
            queryTerms);

        if (normalizedText.Contains(
                normalizedQuery,
                StringComparison.OrdinalIgnoreCase))
        {
            boost += queryTerms.Count;
        }

        return boost;
    }

    private static string NormalizeForSearch(string value)
    {
        return Regex.Replace(
                value.ToLowerInvariant(),
                @"[^a-z0-9]+",
                " ")
            .Trim();
    }

    private async Task<List<VectorSearchResult>> FetchNeighborChunksAsync(
        string documentId,
        int currentChunkNum,
        float baseScore)
    {
        int targetPrev = currentChunkNum - 1;
        int targetNext = currentChunkNum + 1;

        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "documentId",
                        Match = new Qdrant.Client.Grpc.Match { Keyword = documentId }
                    }
                },
                new Condition
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
            limit: 3);

        return neighborResults.Select(n => {
            var res = MapToResult(n);
            res.Score = baseScore * 0.95f;
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
            ChunkNumber = point.Payload.TryGetValue("chunkNumber", out var cn) ? (int)cn.IntegerValue : 0,
            DocumentType = point.Payload.TryGetValue("documentType", out var dt) ? dt.StringValue : string.Empty,
            FileType = point.Payload.TryGetValue("fileType", out var ft) ? ft.StringValue : string.Empty,
            RelativePath = point.Payload.TryGetValue("relativePath", out var rp) ? rp.StringValue : string.Empty,
            PageNumber = point.Payload.TryGetValue("pageNumber", out var pn) ? (int?)pn.IntegerValue : null,
            SheetName = point.Payload.TryGetValue("sheetName", out var sn) ? sn.StringValue : null,
            SqlObjectType = point.Payload.TryGetValue("sqlObjectType", out var sot) ? sot.StringValue : null
        };
    }

    private VectorSearchResult MapRetrievedToResult(RetrievedPoint point)
    {
        return new VectorSearchResult
        {
            Id = point.Id.Num,
            Score = 1.0f,
            Text = point.Payload.TryGetValue("text", out var t) ? t.StringValue : string.Empty,
            ParentText = point.Payload.TryGetValue("parentText", out var pt) ? pt.StringValue : string.Empty,
            DocumentName = point.Payload.TryGetValue("documentName", out var dn) ? dn.StringValue : string.Empty,
            DocumentId = point.Payload.TryGetValue("documentId", out var di) ? di.StringValue : string.Empty,
            ChunkNumber = point.Payload.TryGetValue("chunkNumber", out var cn) ? (int)cn.IntegerValue : 0,
            DocumentType = point.Payload.TryGetValue("documentType", out var dt) ? dt.StringValue : string.Empty,
            FileType = point.Payload.TryGetValue("fileType", out var ft) ? ft.StringValue : string.Empty,
            RelativePath = point.Payload.TryGetValue("relativePath", out var rp) ? rp.StringValue : string.Empty,
            PageNumber = point.Payload.TryGetValue("pageNumber", out var pn) ? (int?)pn.IntegerValue : null,
            SheetName = point.Payload.TryGetValue("sheetName", out var sn) ? sn.StringValue : null,
            SqlObjectType = point.Payload.TryGetValue("sqlObjectType", out var sot) ? sot.StringValue : null
        };
    }

    private static string CleanQueryForFullText(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        // Remove common stop words
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what", "are", "the", "is", "a", "an", "of", "for", "in", "on", "to",
            "and", "or", "mention", "mentioned", "exact", "give", "me", "please",
            "list", "all", "items", "item"
        };

        var words = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !stopWords.Contains(w))
            .Select(w => w.Trim('?', '.', ',', '!', '"', '\''))
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToList();

        return string.Join(" ", words);
    }
}