using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.VectorStore.Interfaces;
using Rag.VectorStore.Models;
using System.Text;
using System.Text.RegularExpressions;

using QdrantMatch = Qdrant.Client.Grpc.Match;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace Rag.VectorStore.Services;

public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly Lazy<Task<IReadOnlyList<RetrievedPoint>>> _payloadCache;

    private const string CollectionName = "document_chunks_2";
    private const ulong VectorSize = 384;

    private const int CandidateMultiplier = 6;
    private const int MinCandidates = 20;
    private const int MaxCandidates = 60;

    public QdrantVectorStore(string host = "localhost")
    {
        _client = new QdrantClient(host);

        _payloadCache = new Lazy<Task<IReadOnlyList<RetrievedPoint>>>(
            LoadAllPayloadsAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
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

        await _client.CreatePayloadIndexAsync(collectionName: CollectionName, fieldName: "tableId", schemaType: PayloadSchemaType.Keyword);

        await _client.CreatePayloadIndexAsync(collectionName: CollectionName, fieldName: "parentBlockId", schemaType: PayloadSchemaType.Keyword);

        await _client.CreatePayloadIndexAsync(collectionName: CollectionName, fieldName: "documentId", schemaType: PayloadSchemaType.Keyword);
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
        string tableId = "",
        string parentBlockId = "",
        string tableContext = "",
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
                ["tableId"] = tableId,
                ["parentBlockId"] = parentBlockId,
                ["tableContext"] = tableContext,
                ["documentName"] = documentName,
                ["documentType"] = documentType,
                ["fileType"] = fileType,
                ["relativePath"] = relativePath,
                ["searchText"] = NormalizeVersionAliases(
                    $"{documentName} {relativePath} {tableContext} {text} {parentText}")
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
        var candidateLimit = Math.Clamp(
            limit * CandidateMultiplier,
            MinCandidates,
            MaxCandidates);

        // ---------- STAGE 1: CANDIDATE RETRIEVAL ----------

        var semanticTask = _client.QueryAsync(
            collectionName: CollectionName,
            query: queryVector,
            limit: (ulong)candidateLimit);

        var normalizedQuery = NormalizeVersionAliases(queryText);
        var cleanedQuery = CleanQueryForFullText(normalizedQuery);

        var textResults = new List<RetrievedPoint>();

        if (!string.IsNullOrWhiteSpace(cleanedQuery))
        {
            var textResponse = await _client.ScrollAsync(
                collectionName: CollectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "searchText",
                                Match = new QdrantMatch
                                {
                                    Text = cleanedQuery
                                }
                            }
                        }
                    }
                },
                limit: (uint)candidateLimit);

            textResults = textResponse.Result?.ToList()
                ?? new List<RetrievedPoint>();
        }

        var lexicalTask = SearchLexicalCandidatesAsync(normalizedQuery);

        await Task.WhenAll(semanticTask, lexicalTask);

        var semanticResults = await semanticTask;
        var lexicalResults = await lexicalTask;

        var combined = new Dictionary<ulong, double>();

        for (var i = 0; i < semanticResults.Count; i++)
        {
            var id = semanticResults[i].Id.Num;
            combined[id] = combined.GetValueOrDefault(id) + 1.0 / (i + 60);
        }

        for (var i = 0; i < textResults.Count; i++)
        {
            var id = textResults[i].Id.Num;
            combined[id] = combined.GetValueOrDefault(id) + 1.0 / (i + 60);
        }

        foreach (var result in lexicalResults)
        {
            combined[result.Id.Num] =
                combined.GetValueOrDefault(result.Id.Num) + 1.0;
        }

        var candidateResults = semanticResults
            .Select(MapToResult)
            .Concat(textResults.Select(MapRetrievedToResult))
            .Concat(lexicalResults.Select(MapRetrievedToResult))
            .GroupBy(x => x.Id)
            .Select(group => group.First())
            .ToList();

        var lexicalScores = candidateResults
            .ToDictionary(
                result => result.Id,
                result => CalculateLexicalBoost(result, normalizedQuery));

        foreach (var candidate in candidateResults)
        {
            combined[candidate.Id] =
                combined.GetValueOrDefault(candidate.Id) +
                lexicalScores.GetValueOrDefault(candidate.Id);
        }

        var queryIdentifiers = ExtractQueryIdentifiers(queryText);

        var identifierMatches = candidateResults
            .Where(result =>
                queryIdentifiers.Count > 0 &&
                ContainsAllIdentifierTokens(result, queryIdentifiers))
            .Select(result => result.Id)
            .ToHashSet();

        foreach (var candidate in candidateResults)
        {
            var baseScore = combined.GetValueOrDefault(candidate.Id);
            candidate.Score = (float)(
                baseScore
                + lexicalScores.GetValueOrDefault(candidate.Id) * 10
                + (identifierMatches.Contains(candidate.Id) ? 10000 : 0));
        }

        var selected = SelectMinimumChunks(queryText, candidateResults);

        var selectedTableIds = selected
            .Where(r => !string.IsNullOrWhiteSpace(r.TableId))
            .Select(r => r.TableId)
            .Distinct()
            .ToList();

        if (selectedTableIds.Count > 0)
        {
            var tableTasks = selectedTableIds
                .Select(id => FetchTableChunksAsync(id, baseScore: 5000))
                .ToList();

            var tableChunks = (await Task.WhenAll(tableTasks))
                .SelectMany(r => r)
                .ToList();

            selected = selected
                .Concat(tableChunks)
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .ToList();
        }

        return selected
            .OrderByDescending(r => r.Score)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .OrderBy(r => r.DocumentId)
            .ThenBy(r => r.ChunkNumber)
            .ToList();
    }

    public async Task<IReadOnlyList<VectorSearchResult>> GetDocumentChunksAsync(
        string documentId)
    {
        var response = await _client.ScrollAsync(
            collectionName: CollectionName,
            filter: new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "documentId",
                            Match = new QdrantMatch { Keyword = documentId }
                        }
                    }
                }
            },
            limit: 10000);

        return (response.Result ?? [])
            .Select(MapRetrievedToResult)
            .Where(r => r.ChunkNumber > 0)
            .OrderBy(r => r.ChunkNumber)
            .ToList();
    }

    // =================================================================
    // DYNAMIC MINIMUM CHUNK SELECTION
    // =================================================================

    private static List<VectorSearchResult> SelectMinimumChunks(
        string queryText,
        IReadOnlyList<VectorSearchResult> candidates)
    {
        if (candidates.Count == 0)
            return new List<VectorSearchResult>();

        var intent = DetectQueryIntent(queryText);

        // WholeSection — retain all retrieved chunks (no score cutoff).
        if (intent == QueryIntent.WholeSection)
        {
            return candidates
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();
        }

        var groups = candidates
            .GroupBy(GetLogicalBlockKey)
            .Select(g => new
            {
                Key = g.Key,
                TopScore = g.Max(x => x.Score),
                Chunks = g.OrderBy(x => x.ChunkNumber).ToList()
            })
            .OrderByDescending(g => g.TopScore)
            .ToList();

        if (groups.Count == 0)
            return new List<VectorSearchResult>();

        var topScore = groups[0].TopScore;
        var scores = groups.Select(g => (double)g.TopScore).ToList();
        var mean = scores.Average();
        var stdDev = Math.Sqrt(scores
            .Select(s => Math.Pow(s - mean, 2))
            .Average());

        if (intent == QueryIntent.SingleFact)
        {
            var best = groups[0];
            var selected = new List<VectorSearchResult>(best.Chunks);

            if (best.Chunks.Count > 0)
            {
                var bestMaxChunk = best.Chunks.Max(c => c.ChunkNumber);
                var continuation = candidates
                    .Where(c => c.ChunkNumber == bestMaxChunk + 1 &&
                                selected.All(s => s.Id != c.Id))
                    .ToList();
                selected.AddRange(continuation);
            }

            return selected;
        }

        // General intent — adaptive cutoff across retrieved logical blocks.
        const double ratio = 0.55;
        const int maxUnits = 12;

        var cutoff = Math.Max(topScore * ratio, mean + stdDev * 0.25);

        var picked = groups
            .Where(g => g.TopScore >= cutoff)
            .Take(maxUnits)
            .ToList();

        if (picked.Count == 0)
            picked = groups.Take(1).ToList();

        var result = picked
            .SelectMany(g => g.Chunks)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderByDescending(c => c.Score)
            .ToList();

        return result
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();
    }

    private enum QueryIntent
    {
        SingleFact,
        WholeSection,
        General
    }

    private static QueryIntent DetectQueryIntent(string query)
    {
        var q = query.ToLowerInvariant();

        if (Regex.IsMatch(q, @"\b(all|every|list|complete|entire|full|enumerate)\b"))
            return QueryIntent.WholeSection;

        if (Regex.IsMatch(q, @"\bwhat\s+are\b") &&
            Regex.IsMatch(q, @"\b(requirements?|rules?|constraints?|policies|guidelines|features|items?|sections?|categories|types?|roles?|responsibilities|objectives|goals|scopes?|steps?|phases?)\b"))
            return QueryIntent.WholeSection;

        if (Regex.IsMatch(q, @"\b(give|show|provide|tell)\s+(me\s+)?(the\s+)?") &&
            Regex.IsMatch(q, @"\b(requirements?|rules?|constraints?|list|features|items?|sections?)\b"))
            return QueryIntent.WholeSection;

        if (Regex.IsMatch(q, @"\bwhat\s+is\b") &&
            !Regex.IsMatch(q, @"\bwhat\s+are\b"))
            return QueryIntent.SingleFact;

        if (Regex.IsMatch(q, @"\bhow\s+(many|much)\b"))
            return QueryIntent.SingleFact;

        return QueryIntent.General;
    }

    // =================================================================
    // HELPERS
    // =================================================================

    private static string GetLogicalBlockKey(VectorSearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.TableId))
            return $"table:{result.TableId}";

        if (!string.IsNullOrWhiteSpace(result.ParentBlockId))
            return $"parent:{result.ParentBlockId}";

        return $"chunk:{result.DocumentId}:{result.ChunkNumber}";
    }

    private async Task<List<VectorSearchResult>> FetchTableChunksAsync(
        string tableId,
        float baseScore)
    {
        var response = await _client.ScrollAsync(
            collectionName: CollectionName,
            filter: new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "tableId",
                            Match = new QdrantMatch
                            {
                                Keyword = tableId
                            }
                        }
                    }
                }
            },
            limit: 1000);

        return (response.Result ?? [])
            .Select(point =>
            {
                var result = MapRetrievedToResult(point);
                result.Score = baseScore - result.ChunkNumber;
                return result;
            })
            .OrderBy(result => result.ChunkNumber)
            .ToList();
    }

    private async Task<IReadOnlyList<RetrievedPoint>> LoadAllPayloadsAsync()
    {
        var response = await _client.ScrollAsync(
            collectionName: CollectionName,
            limit: 10000);

        return response.Result?.ToList() ?? [];
    }

    private async Task<IReadOnlyList<RetrievedPoint>> SearchLexicalCandidatesAsync(
        string query)
    {
        var terms = Tokenize(query)
            .Where(term => term.Length >= 2 || term.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (terms.Count == 0)
            return Array.Empty<RetrievedPoint>();

        var points = await _payloadCache.Value;

        return points
            .Select(point => new
            {
                Point = point,
                Score = CalculateLexicalPayloadScore(point, terms, query)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Point)
            .ToList();
    }

    private static int CalculateLexicalPayloadScore(
        RetrievedPoint point,
        IReadOnlyList<string> terms,
        string query)
    {
        var searchableText = string.Join(
            " ",
            GetPayloadValue(point, "documentName"),
            GetPayloadValue(point, "relativePath"),
            GetPayloadValue(point, "text"),
            GetPayloadValue(point, "parentText"),
            GetPayloadValue(point, "tableId"),
            GetPayloadValue(point, "tableContext"),
            GetPayloadValue(point, "searchText"));

        var documentTerms = Tokenize(searchableText).ToList();
        var score = CalculateTokenMatchScore(documentTerms, terms);

        var queryIdentifiers = ExtractQueryIdentifiers(query);
        var documentIdentifiers = ExtractQueryIdentifiers(searchableText);

        if (queryIdentifiers.Any(identifier =>
                documentIdentifiers.Contains(identifier, StringComparer.OrdinalIgnoreCase)))
        {
            score += 100;
        }

        return score;
    }

    private static string GetPayloadValue(RetrievedPoint point, string key)
    {
        return point.Payload.TryGetValue(key, out var value)
            ? value.StringValue
            : string.Empty;
    }

    private static string GetPayloadValue(ScoredPoint point, string key)
    {
        return point.Payload.TryGetValue(key, out var value)
            ? value.StringValue
            : string.Empty;
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what", "which", "who", "where", "when", "why", "how", "are", "is", "the", "a",
            "an", "of", "for", "in", "on", "to", "and", "or", "from", "by", "with", "does",
            "do", "can", "should", "were", "was", "be", "this", "that", "all", "any", "name", "give",
            "me", "please", "tell"
        };

        return Regex.Matches(value.ToLowerInvariant(), @"[a-z]+|[0-9]+")
            .Select(match => match.Value)
            .Select(NormalizeLexicalTerm)
            .Where(term => !stopWords.Contains(term))
            .ToList();
    }

    private static string NormalizeLexicalTerm(string term)
    {
        if (term.Length > 4 &&
            term.EndsWith("ies", StringComparison.Ordinal))
        {
            return term[..^3] + "y";
        }

        if (term.Length > 3 &&
            term.EndsWith('s') &&
            !term.EndsWith("ss", StringComparison.Ordinal) &&
            !term.EndsWith("us", StringComparison.Ordinal) &&
            !term.EndsWith("is", StringComparison.Ordinal))
        {
            return term[..^1];
        }

        return term;
    }

    private static int CalculateTokenMatchScore(
        IReadOnlyList<string> documentTerms,
        IReadOnlyList<string> queryTerms)
    {
        var documentSet = documentTerms.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var score = queryTerms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(term => documentSet.Contains(term)
                ? (term.Any(char.IsDigit) ? 10 : 1)
                : 0);

        for (var length = Math.Min(4, queryTerms.Count); length >= 2; length--)
        {
            for (var start = 0; start <= queryTerms.Count - length; start++)
            {
                var phrase = queryTerms.Skip(start).Take(length).ToList();

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
            $"{result.DocumentName} " +
            $"{result.RelativePath} " +
            $"{result.Text} " +
            $"{result.ParentText}";

        var normalizedQuery = NormalizeForSearch(query);
        var normalizedText = NormalizeForSearch(
            NormalizeVersionAliases(searchableText));

        if (string.IsNullOrWhiteSpace(normalizedQuery) ||
            string.IsNullOrWhiteSpace(normalizedText))
        {
            return 0;
        }

        var queryTerms = Tokenize(query)
            .Where(term => term.Length >= 2 || term.Any(char.IsDigit))
            .ToList();

        double boost = CalculateTokenMatchScore(
            Tokenize(normalizedText), queryTerms);

        var queryIdentifiers = ExtractQueryIdentifiers(query);
        var documentIdentifiers = ExtractQueryIdentifiers(searchableText);

        foreach (var identifier in queryIdentifiers)
        {
            if (documentIdentifiers.Contains(identifier, StringComparer.OrdinalIgnoreCase))
                boost += 100;
        }

        if (normalizedText.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            boost += queryTerms.Count;

        return boost;
    }

    private static string NormalizeForSearch(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    }

    public static string NormalizeVersionAliases(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return Regex.Replace(
            value,
            @"\b(?:version|v)\s*:?\s*(\d+(?:\.\d+)+)\b",
            match =>
            {
                var version = match.Groups[1].Value;
                return $"version {version} v{version} v {version}";
            },
            RegexOptions.IgnoreCase);
    }

    private static IReadOnlyList<string> ExtractQueryIdentifiers(string query)
    {
        return Regex.Matches(
                query,
                @"\b[A-Za-z][A-Za-z0-9]*(?:[-_][A-Za-z0-9]+)+\b|\b(?:v(?:ersion)?\s*)?\d+(?:\.\d+)+\b",
                RegexOptions.IgnoreCase)
            .Select(match => match.Value)
            .Select(NormalizeIdentifier)
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeIdentifier(string value)
    {
        var versionMatch = Regex.Match(
            value,
            @"^(?:v(?:ersion)?\s*)?(\d+(?:\.\d+)+)$",
            RegexOptions.IgnoreCase);

        if (versionMatch.Success)
            return versionMatch.Groups[1].Value;

        return value.Trim().ToLowerInvariant();
    }

    private static bool ContainsAllIdentifierTokens(
        VectorSearchResult result,
        IReadOnlyList<string> identifiers)
    {
        var searchableText = NormalizeForSearch(
            $"{result.DocumentName} " +
            $"{result.RelativePath} " +
            $"{result.Text} " +
            $"{result.ParentText}");

        return identifiers.All(identifier =>
            searchableText.Contains(
                NormalizeForSearch(identifier),
                StringComparison.OrdinalIgnoreCase));
    }

    private VectorSearchResult MapToResult(ScoredPoint point)
    {
        return new VectorSearchResult
        {
            Id = point.Id.Num,
            Score = point.Score,
            Text = GetPayloadValue(point, "text"),
            ParentText = GetPayloadValue(point, "parentText"),
            TableId = GetPayloadValue(point, "tableId"),
            ParentBlockId = GetPayloadValue(point, "parentBlockId"),
            TableContext = GetPayloadValue(point, "tableContext"),
            DocumentName = GetPayloadValue(point, "documentName"),
            DocumentId = GetPayloadValue(point, "documentId"),
            ChunkNumber = point.Payload.TryGetValue("chunkNumber", out var cn) ? (int)cn.IntegerValue : 0,
            DocumentType = GetPayloadValue(point, "documentType"),
            FileType = GetPayloadValue(point, "fileType"),
            RelativePath = GetPayloadValue(point, "relativePath"),
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
            Text = GetPayloadValue(point, "text"),
            ParentText = GetPayloadValue(point, "parentText"),
            TableId = GetPayloadValue(point, "tableId"),
            ParentBlockId = GetPayloadValue(point, "parentBlockId"),
            TableContext = GetPayloadValue(point, "tableContext"),
            DocumentName = GetPayloadValue(point, "documentName"),
            DocumentId = GetPayloadValue(point, "documentId"),
            ChunkNumber = point.Payload.TryGetValue("chunkNumber", out var cn) ? (int)cn.IntegerValue : 0,
            DocumentType = GetPayloadValue(point, "documentType"),
            FileType = GetPayloadValue(point, "fileType"),
            RelativePath = GetPayloadValue(point, "relativePath"),
            PageNumber = point.Payload.TryGetValue("pageNumber", out var pn) ? (int?)pn.IntegerValue : null,
            SheetName = point.Payload.TryGetValue("sheetName", out var sn) ? sn.StringValue : null,
            SqlObjectType = point.Payload.TryGetValue("sqlObjectType", out var sot) ? sot.StringValue : null
        };
    }

    private static string CleanQueryForFullText(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what", "are", "the", "is", "a", "an", "of", "for", "in", "on", "to", "and",
            "or", "mention", "mentioned", "exact", "give", "me", "please", "list", "all",
            "items", "item"
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