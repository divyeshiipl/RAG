using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.VectorStore.Interfaces;
using Rag.VectorStore.Models;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

using QdrantMatch = Qdrant.Client.Grpc.Match;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace Rag.VectorStore.Services;

public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly Lazy<Task<IReadOnlyList<RetrievedPoint>>> _payloadCache;
    private readonly Lazy<Task<IReadOnlyList<StructuredPayloadRows>>> _structuredRowsCache;
    private readonly Lazy<Task<Bm25Index>> _bm25Index;

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
        _structuredRowsCache = new Lazy<Task<IReadOnlyList<StructuredPayloadRows>>>(
            LoadStructuredRowsAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _bm25Index = new Lazy<Task<Bm25Index>>(
            LoadBm25IndexAsync,
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

        await _client.CreatePayloadIndexAsync(collectionName: CollectionName, fieldName: "sheetName", schemaType: PayloadSchemaType.Keyword);
        await _client.CreatePayloadIndexAsync(collectionName: CollectionName, fieldName: "rowIdentifiers", schemaType: PayloadSchemaType.Keyword);
        await _client.CreatePayloadIndexAsync(collectionName: CollectionName, fieldName: "rowStartIndex", schemaType: PayloadSchemaType.Integer);
        await _client.CreatePayloadIndexAsync(collectionName: CollectionName, fieldName: "rowEndIndex", schemaType: PayloadSchemaType.Integer);
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
        string? sqlObjectType = null,
        int? rowStartIndex = null,
        int? rowEndIndex = null,
        IReadOnlyList<string>? rowIdentifiers = null,
        string structuredRowsJson = "")
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
                    $"{documentName} {relativePath} {sheetName} {tableContext} " +
                    $"{string.Join(" ", rowIdentifiers ?? Array.Empty<string>())} {text} {parentText}"),
                ["structuredRowsJson"] = structuredRowsJson
            }
        };

        if (pageNumber.HasValue)
            point.Payload["pageNumber"] = pageNumber.Value;

        if (!string.IsNullOrWhiteSpace(sheetName))
            point.Payload["sheetName"] = sheetName;

        if (!string.IsNullOrWhiteSpace(sqlObjectType))
            point.Payload["sqlObjectType"] = sqlObjectType;
        if (rowStartIndex.HasValue)
            point.Payload["rowStartIndex"] = rowStartIndex.Value;
        if (rowEndIndex.HasValue)
            point.Payload["rowEndIndex"] = rowEndIndex.Value;
        if (rowIdentifiers is { Count: > 0 })
        {
            var normalizedRowIdentifiers = rowIdentifiers
                .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
                .Select(NormalizeIdentifierValue)
                .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (normalizedRowIdentifiers.Length > 0)
                point.Payload["rowIdentifiers"] = normalizedRowIdentifiers;
        }

        await _client.UpsertAsync(CollectionName, new[] { point });
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        string queryText,
        int limit = 5)
    {
        var normalizedQuery = NormalizeVersionAliases(queryText);
        var structuredSearch = await SearchStructuredRowsAsync(normalizedQuery);
        if (structuredSearch.IsApplicable && structuredSearch.Results.Count > 0)
            return structuredSearch.Results;

        var candidateLimit = Math.Clamp(
            limit * CandidateMultiplier,
            MinCandidates,
            MaxCandidates);

        // ---------- STAGE 1: CANDIDATE RETRIEVAL ----------

        var semanticTask = _client.QueryAsync(
            collectionName: CollectionName,
            query: queryVector,
            limit: (ulong)candidateLimit);

        var lexicalTask = SearchLexicalCandidatesAsync(
            normalizedQuery,
            candidateLimit);

        await Task.WhenAll(semanticTask, lexicalTask);

        var semanticResults = await semanticTask;
        var lexicalResults = await lexicalTask;
        var queryIdentifiers = ExtractQueryIdentifiers(normalizedQuery);
        var exactIdentifierPoints = queryIdentifiers.Count == 0
            ? Array.Empty<RetrievedPoint>()
            : (await _payloadCache.Value)
                .Where(point => queryIdentifiers.Any(identifier =>
                    ContainsIdentifier(point, identifier)))
                .ToArray();
        var exactMetadataPoints = queryIdentifiers.Count == 0
            ? Array.Empty<RetrievedPoint>()
            : (await _payloadCache.Value)
                .Where(point => queryIdentifiers.Any(identifier =>
                    ContainsIdentifierInMetadata(point, identifier)))
                .ToArray();
        var exactSourcePoints = exactMetadataPoints.Length > 0
            ? exactMetadataPoints
            : exactIdentifierPoints;
        var exactIdentifierDocumentIds = exactSourcePoints
            .Select(point => GetPayloadValue(point, "documentId"))
            .Where(documentId => !string.IsNullOrWhiteSpace(documentId))
            .ToHashSet(StringComparer.Ordinal);

        var candidateResults = semanticResults
            .Select(MapToResult)
            .Concat(lexicalResults.Select(MapRetrievedToResult))
            .Concat(exactSourcePoints.Select(MapRetrievedToResult))
            .GroupBy(x => x.Id)
            .Select(group => group.First())
            .ToList();
        if (exactIdentifierDocumentIds.Count > 0)
        {
            candidateResults = candidateResults
                .Where(result => exactIdentifierDocumentIds.Contains(result.DocumentId))
                .ToList();
        }

        const int reciprocalRankConstant = 60;
        var fusedScores = new Dictionary<ulong, double>();

        for (var rank = 0; rank < semanticResults.Count; rank++)
        {
            var id = semanticResults[rank].Id.Num;
            fusedScores[id] = fusedScores.GetValueOrDefault(id) +
                1d / (reciprocalRankConstant + rank + 1);
        }

        for (var rank = 0; rank < lexicalResults.Count; rank++)
        {
            var id = lexicalResults[rank].Id.Num;
            fusedScores[id] = fusedScores.GetValueOrDefault(id) +
                1d / (reciprocalRankConstant + rank + 1);
        }

        var identifierMatches = candidateResults
            .Where(result =>
                queryIdentifiers.Count > 0 &&
                ContainsAllIdentifierTokens(result, queryIdentifiers))
            .Select(result => result.Id)
            .ToHashSet();

        foreach (var candidate in candidateResults)
        {
            candidate.Score = (float)(
                fusedScores.GetValueOrDefault(candidate.Id) * 1000 +
                (identifierMatches.Contains(candidate.Id) ? 1000 : 0) +
                (queryIdentifiers.Any(identifier =>
                    ContainsIdentifierInMetadata(candidate, identifier))
                    ? 2000
                    : 0) +
                GetQueryCoverageScore(candidate, normalizedQuery));
        }

        var selected = SelectMinimumChunks(queryText, candidateResults);
        selected = await AddAdjacentChunksAsync(selected);

        var rankedResults = selected
            .OrderByDescending(r => r.Score)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToList();

        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        var textResults = rankedResults
            .Where(result =>
            {
                var normalizedContent = NormalizeForSearch(result.Text);
                return string.IsNullOrWhiteSpace(normalizedContent) ||
                       seenContent.Add(normalizedContent);
            })
            .OrderBy(r => r.DocumentId)
            .ThenBy(r => r.ChunkNumber)
            .ToList();

        var relevantDocumentIds = textResults
            .Select(result => result.DocumentId)
            .Where(documentId => !string.IsNullOrWhiteSpace(documentId))
            .ToHashSet(StringComparer.Ordinal);
        if (relevantDocumentIds.Count == 0)
            return textResults;

        structuredSearch = await SearchStructuredRowsAsync(
            normalizedQuery,
            relevantDocumentIds);
        return structuredSearch.IsApplicable && structuredSearch.Results.Count > 0
            ? structuredSearch.Results
            : textResults;
    }

    private async Task<List<VectorSearchResult>> AddAdjacentChunksAsync(
        IReadOnlyList<VectorSearchResult> selected)
    {
        if (selected.Count == 0)
            return selected.ToList();

        var points = await _payloadCache.Value;
        var pointsByDocumentAndChunk = points
            .Where(point =>
                point.Payload.TryGetValue("chunkNumber", out var chunkNumber) &&
                chunkNumber.KindCase == Value.KindOneofCase.IntegerValue)
            .GroupBy(point => (
                DocumentId: GetPayloadValue(point, "documentId"),
                ChunkNumber: (int)point.Payload["chunkNumber"].IntegerValue))
            .ToDictionary(group => group.Key, group => group.First());
        var expanded = selected.ToDictionary(result => result.Id);

        foreach (var seed in selected
                     .GroupBy(result => result.DocumentId, StringComparer.Ordinal)
                     .SelectMany(group => group
                         .OrderByDescending(result => result.Score)
                         .Take(3)))
        {
            if (string.IsNullOrWhiteSpace(seed.DocumentId) ||
                seed.ChunkNumber <= 0)
            {
                continue;
            }

            foreach (var adjacentChunkNumber in new[]
                     {
                         seed.ChunkNumber - 1,
                         seed.ChunkNumber + 1
                     })
            {
                if (adjacentChunkNumber <= 0 ||
                    !pointsByDocumentAndChunk.TryGetValue(
                        (seed.DocumentId, adjacentChunkNumber),
                        out var point) ||
                    expanded.ContainsKey(point.Id.Num))
                {
                    continue;
                }

                var adjacent = MapRetrievedToResult(point);
                adjacent.Score = seed.Score * 0.8f;
                expanded.Add(adjacent.Id, adjacent);
            }
        }

        return expanded.Values.ToList();
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

    public async Task<IReadOnlyList<VectorSearchResult>> SearchByRowIdentifierAsync(
        string rowIdentifier,
        string? documentId = null,
        string? sheetName = null)
    {
        if (string.IsNullOrWhiteSpace(rowIdentifier))
            throw new ArgumentException("A row identifier is required.", nameof(rowIdentifier));

        var normalizedIdentifier = NormalizeIdentifierValue(rowIdentifier);
        var filter = new Filter();
        filter.Must.Add(new Condition
        {
            Field = new FieldCondition
            {
                Key = "rowIdentifiers",
                Match = new QdrantMatch { Keyword = normalizedIdentifier }
            }
        });

        if (!string.IsNullOrWhiteSpace(documentId))
        {
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "documentId",
                    Match = new QdrantMatch { Keyword = documentId }
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(sheetName))
        {
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "sheetName",
                    Match = new QdrantMatch { Keyword = sheetName }
                }
            });
        }

        var response = await _client.ScrollAsync(
            collectionName: CollectionName,
            filter: filter,
            limit: 1000);

        var matches = (response.Result ?? [])
            .Select(MapRetrievedToResult)
            .ToList();

        if (matches.Count > 0)
            return matches
                .OrderBy(result => result.DocumentId)
                .ThenBy(result => result.SheetName)
                .ThenBy(result => result.RowStartIndex)
                .ToList();

        var fallback = await _client.ScrollAsync(
            collectionName: CollectionName,
            limit: 1000);

        return (fallback.Result ?? [])
            .Select(MapRetrievedToResult)
            .Where(result => result.RowIdentifiers.Any(identifier =>
                string.Equals(NormalizeIdentifierValue(identifier), normalizedIdentifier, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(result => result.DocumentId)
            .ThenBy(result => result.SheetName)
            .ThenBy(result => result.RowStartIndex)
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
        var rerankedCandidates = candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = GetConfidenceScore(candidate)
            })
            .ToList();
        var topScore = rerankedCandidates.Max(candidate => candidate.Score);
        var scores = rerankedCandidates
            .Select(candidate => candidate.Score)
            .ToList();
        var mean = scores.Average();
        var stdDev = Math.Sqrt(scores
            .Select(score => Math.Pow(score - mean, 2))
            .Average());
        var chunkCutoff = Math.Min(
            topScore,
            Math.Max(topScore * 0.65, mean + stdDev * 0.25));
        var confidentCandidates = rerankedCandidates
            .Where(candidate => candidate.Score >= chunkCutoff)
            .Select(candidate => candidate.Candidate)
            .ToList();

        if (intent == QueryIntent.WholeSection)
        {
            return confidentCandidates
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();
        }

        var groups = confidentCandidates
            .GroupBy(GetLogicalBlockKey)
            .Select(g => new
            {
                Key = g.Key,
                TopScore = g.Max(GetConfidenceScore),
                Chunks = g.OrderBy(x => x.ChunkNumber).ToList()
            })
            .OrderByDescending(g => g.TopScore)
            .ToList();

        if (groups.Count == 0)
            return new List<VectorSearchResult>();

        var topGroupScore = groups[0].TopScore;
        var groupScores = groups.Select(g => (double)g.TopScore).ToList();
        var groupMean = groupScores.Average();
        var groupStdDev = Math.Sqrt(groupScores
            .Select(s => Math.Pow(s - groupMean, 2))
            .Average());

        if (intent == QueryIntent.SingleFact)
        {
            var best = groups[0];
            var selected = new List<VectorSearchResult>(best.Chunks);

            if (best.Chunks.Count > 0)
            {
                var bestMaxChunk = best.Chunks.Max(c => c.ChunkNumber);
                var continuation = confidentCandidates
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

        var cutoff = Math.Max(
            topGroupScore * ratio,
            groupMean + groupStdDev * 0.25);

        var picked = groups
            .Where(g => g.TopScore >= cutoff)
            .Take(maxUnits)
            .ToList();

        if (picked.Count == 0)
            picked = groups.Take(1).ToList();

        var result = picked
            .SelectMany(g => g.Chunks)
            .Where(chunk => GetConfidenceScore(chunk) >= chunkCutoff)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderByDescending(c => c.Score)
            .ToList();

        return result
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();
    }

    private static double GetConfidenceScore(VectorSearchResult candidate) =>
        Math.Max(0, candidate.Score);

    private static double GetQueryCoverageScore(
        VectorSearchResult candidate,
        string query)
    {
        var queryTerms = Tokenize(query)
            .Where(term => term.Length >= 2 || term.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (queryTerms.Count == 0)
            return 0;

        var searchableText = NormalizeForSearch(string.Join(
            " ",
            candidate.DocumentName,
            candidate.RelativePath,
            candidate.Text,
            candidate.TableContext,
            string.Join(" ", candidate.RowIdentifiers)));
        var matchingTerms = queryTerms.Count(term =>
            searchableText.Contains(
                NormalizeForSearch(term),
                StringComparison.OrdinalIgnoreCase));

        return 40d * matchingTerms / queryTerms.Count +
               GetTableRelationScore(candidate, queryTerms);
    }

    private static double GetTableRelationScore(
        VectorSearchResult candidate,
        IReadOnlyList<string> queryTerms)
    {
        var source = string.Join(Environment.NewLine, candidate.Text, candidate.ParentText);
        var lines = source.Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var score = 0d;

        for (var lineIndex = 0; lineIndex + 2 < lines.Length; lineIndex++)
        {
            if (!IsMarkdownTableRow(lines[lineIndex]) ||
                !IsMarkdownTableSeparator(lines[lineIndex + 1]))
            {
                continue;
            }

            var headers = SplitMarkdownTableRow(lines[lineIndex]);
            if (headers.Count == 0)
                continue;

            var targetColumns = headers
                .Select((header, index) => new
                {
                    Index = index,
                    Terms = Tokenize(header)
                })
                .Where(header => header.Terms.Any(queryTerms.Contains))
                .Select(header => header.Index)
                .ToHashSet();
            if (targetColumns.Count == 0)
                continue;

            for (var rowIndex = lineIndex + 2;
                 rowIndex < lines.Length && IsMarkdownTableRow(lines[rowIndex]);
                 rowIndex++)
            {
                var cells = SplitMarkdownTableRow(lines[rowIndex]);
                var matchedEntityColumns = cells
                    .Select((cell, index) => new { Index = index, Cell = cell })
                    .Where(item =>
                        !targetColumns.Contains(item.Index) &&
                        Tokenize(item.Cell).Any(queryTerms.Contains))
                    .Select(item => item.Index)
                    .ToList();
                if (matchedEntityColumns.Count == 0)
                    continue;

                score += 90;
            }

            lineIndex++;
        }

        return score;
    }

    private static bool IsMarkdownTableRow(string line) =>
        line.StartsWith('|') && line.EndsWith('|');

    private static bool IsMarkdownTableSeparator(string line)
    {
        if (!IsMarkdownTableRow(line))
            return false;

        return SplitMarkdownTableRow(line).All(cell =>
            Regex.IsMatch(cell, @"^:?-{3,}:?$"));
    }

    private static IReadOnlyList<string> SplitMarkdownTableRow(string line) =>
        line.Split('|')
            .Skip(1)
            .SkipLast(1)
            .Select(cell => cell.Trim())
            .ToList();

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

    private async Task<IReadOnlyList<RetrievedPoint>> LoadAllPayloadsAsync()
    {
        const uint pageSize = 1000;
        var points = new List<RetrievedPoint>();
        PointId? offset = null;

        do
        {
            var response = await _client.ScrollAsync(
                collectionName: CollectionName,
                limit: pageSize,
                offset: offset);

            if (response.Result is { Count: > 0 })
                points.AddRange(response.Result);

            offset = response.NextPageOffset is { HasNum: true } or { HasUuid: true }
                ? response.NextPageOffset
                : null;
        }
        while (offset is not null);

        return points;
    }

    private async Task<StructuredSearchResult> SearchStructuredRowsAsync(
        string query,
        IReadOnlySet<string>? relevantDocumentIds = null)
    {
        var payloadRows = await _structuredRowsCache.Value;
        if (payloadRows.Count == 0)
            return StructuredSearchResult.NotApplicable;
        if (relevantDocumentIds is not null)
        {
            if (relevantDocumentIds.Count == 0)
                return StructuredSearchResult.NotApplicable;
            payloadRows = payloadRows
                .Where(payload => relevantDocumentIds.Contains(
                    GetPayloadValue(payload.Point, "documentId")))
                .ToList();
        }
        if (payloadRows.Count == 0)
            return StructuredSearchResult.NotApplicable;

        var rowsByTable = payloadRows
            .SelectMany(payload => payload.Rows.EnumerateArray()
                .Where(row => row.TryGetProperty("fields", out var fields) &&
                              fields.ValueKind == JsonValueKind.Object)
                .Select(row => new StructuredRowEntry(payload, row, false)))
            .GroupBy(entry => GetStructuredTableKey(entry.Payload))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<StructuredRowEntry>)group.ToList(),
                StringComparer.Ordinal);
        var filtersByTable = ExtractObservedFilters(query, rowsByTable);
        var requestedTableKeys = FindRequestedTableKeys(query, payloadRows);
        var queryTerms = GetStructuredQueryTerms(query);
        var headerMatchesByTable = rowsByTable.ToDictionary(
            table => table.Key,
            table =>
            {
                var headers = table.Value.SelectMany(entry => entry.Row.GetProperty("fields")
                    .EnumerateObject()
                    .Select(field => field.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var headerScore = CountMentionedHeaders(query, headers);
                var tableTerms = new[]
                    {
                        GetPayloadValue(table.Value[0].Payload.Point, "sheetName"),
                        GetPayloadValue(table.Value[0].Payload.Point, "tableContext")
                    }
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .SelectMany(GetStructuredQueryTerms)
                    .Distinct(StringComparer.Ordinal)
                    .Count(queryTerms.Contains);
                return headerScore * 100 + tableTerms * 1000;
            },
            StringComparer.Ordinal);
        var highestHeaderMatchCount = headerMatchesByTable.Values
            .DefaultIfEmpty()
            .Max();
        var hasYearFilter = Regex.IsMatch(
            query,
            @"(?<![\p{L}\p{N}_-])(?:19|20)\d{2}(?![\p{L}\p{N}_-])");
        var hasStructuredOperation =
            GetStructuredExtremum(query) is not null ||
            GetStructuredComparison(query) is not null ||
            hasYearFilter ||
            Regex.IsMatch(
                query,
                @"\b(?:more\s+than\s+once|multiple\s+times|duplicat(?:e|es|ed|ion)|repeated)\b",
                RegexOptions.IgnoreCase);
        var hasExactStructuredIdentifier = ExtractQueryIdentifiers(query)
            .Any(identifier => rowsByTable.Values
                .SelectMany(rows => rows)
                .Any(entry => entry.Row.GetProperty("fields")
                    .EnumerateObject()
                    .Any(field => string.Equals(
                        NormalizeStructuredValue(GetStructuredScalarValue(field.Value)),
                        NormalizeStructuredValue(identifier),
                        StringComparison.Ordinal))));
        var hasExplicitTableReference = requestedTableKeys.Count > 0 &&
            payloadRows
                .Where(payload => requestedTableKeys.Contains(
                    GetStructuredTableKey(payload)))
                .SelectMany(payload => new[]
                {
                    GetPayloadValue(payload.Point, "sheetName"),
                    GetPayloadValue(payload.Point, "tableContext")
                })
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Any(label => Regex.IsMatch(
                    NormalizeStructuredValue(query),
                    $@"\b(?:in|from|of|within|for)\s+{Regex.Escape(NormalizeStructuredValue(label))}\b",
                    RegexOptions.IgnoreCase));
        var requestsCompleteSet = Regex.IsMatch(
            query,
            @"\b(?:all|every|each|list|enumerate)\b",
            RegexOptions.IgnoreCase);
        var hasDuplicateOperation = Regex.IsMatch(
            query,
            @"\b(?:more\s+than\s+once|multiple\s+times|duplicat(?:e|es|ed|ion)|repeated)\b",
            RegexOptions.IgnoreCase);
        var hasStrongStructuredIntent =
            hasExplicitTableReference ||
            hasExactStructuredIdentifier ||
            hasStructuredOperation && highestHeaderMatchCount > 0 ||
            hasDuplicateOperation && highestHeaderMatchCount > 0 ||
            requestsCompleteSet && filtersByTable.Count > 0 ||
            relevantDocumentIds is not null && filtersByTable.Count > 0;
        if (!hasStrongStructuredIntent)
            return StructuredSearchResult.NotApplicable;

        if (filtersByTable.Count == 0 &&
            requestedTableKeys.Count == 0 &&
            highestHeaderMatchCount == 0)
        {
            return StructuredSearchResult.NotApplicable;
        }

        var targetTableKeys = requestedTableKeys.Count > 0
            ? requestedTableKeys
            : highestHeaderMatchCount > 0
                ? headerMatchesByTable
                    .Where(item => item.Value == highestHeaderMatchCount)
                    .Select(item => item.Key)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        var matchingRows = new List<StructuredRowEntry>();

        if (targetTableKeys.Count == 0)
        {
            var tableMatches = rowsByTable
                .Where(group => filtersByTable.ContainsKey(group.Key))
                .Select(group => new
                {
                    group.Key,
                    Rows = group.Value
                        .Where(entry => MatchesStructuredFilters(
                            entry.Row,
                            filtersByTable[group.Key]))
                        .Select(entry => entry with { MatchesFilters = true })
                        .ToList(),
                    ExplicitHeaderMatches = headerMatchesByTable[group.Key]
                })
                .Where(group => group.Rows.Count > 0)
                .ToList();

            if (tableMatches.Count == 0)
                return new StructuredSearchResult(true, Array.Empty<VectorSearchResult>());

            var highestExplicitHeaderMatches = tableMatches
                .Max(group => group.ExplicitHeaderMatches);
            foreach (var tableMatch in tableMatches.Where(group =>
                         highestExplicitHeaderMatches == 0 ||
                         group.ExplicitHeaderMatches == highestExplicitHeaderMatches))
            {
                matchingRows.AddRange(tableMatch.Rows);
            }
        }
        else
        {
            foreach (var targetKey in targetTableKeys)
            {
                if (!rowsByTable.TryGetValue(targetKey, out var targetRows))
                    continue;

                var targetFilters = filtersByTable.GetValueOrDefault(targetKey) ?? [];
                var directMatches = targetRows
                    .Where(entry => targetFilters.Count > 0 &&
                                    MatchesStructuredFilters(entry.Row, targetFilters))
                    .Select(entry => entry with { MatchesFilters = true })
                    .ToList();

                if (directMatches.Count > 0)
                {
                    matchingRows.AddRange(directMatches);
                    continue;
                }

                var sourceRows = rowsByTable
                    .Where(group => group.Key != targetKey &&
                                    filtersByTable.ContainsKey(group.Key))
                    .SelectMany(group =>
                    {
                        var filters = filtersByTable[group.Key];
                        return group.Value
                            .Where(entry => MatchesStructuredFilters(entry.Row, filters))
                            .Select(entry => entry with { MatchesFilters = true });
                    })
                    .ToList();
                var joinedRows = JoinStructuredRows(sourceRows, targetRows);
                if (joinedRows.Count > 0)
                {
                    matchingRows.AddRange(joinedRows);
                }
                else if (targetFilters.Count == 0)
                {
                    matchingRows.AddRange(targetRows);
                }
            }
        }

        matchingRows = ApplyStructuredQueryOperation(query, matchingRows);

        if (matchingRows.Count == 0)
            return new StructuredSearchResult(true, Array.Empty<VectorSearchResult>());

        var candidates = matchingRows
            .GroupBy(entry => entry.Payload.Point.Id.Num)
            .Select(group =>
            {
                var payload = group.First().Payload;
                var rows = group
                    .Select(entry => entry.Row)
                    .Distinct(new JsonElementRowComparer())
                    .OrderBy(row => row.TryGetProperty("rowIndex", out var index)
                        ? index.GetInt32()
                        : int.MaxValue)
                    .ToList();
                var mapped = MapRetrievedToResult(payload.Point);
                mapped.Text = string.Join(
                    Environment.NewLine,
                    rows.SelectMany(GetStructuredRowTextParts)
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .Distinct(StringComparer.Ordinal));
                mapped.ParentText = string.Join(
                    Environment.NewLine,
                    rows.SelectMany(GetStructuredHeaderParts)
                        .Distinct(StringComparer.Ordinal));
                mapped.Score = 1;
                mapped.RowStartIndex = rows
                    .Where(row => row.TryGetProperty("rowIndex", out _))
                    .Select(row => row.GetProperty("rowIndex").GetInt32())
                    .DefaultIfEmpty(mapped.RowStartIndex ?? 0)
                    .Min();
                mapped.RowEndIndex = rows
                    .Where(row => row.TryGetProperty("rowIndex", out _))
                    .Select(row => row.GetProperty("rowIndex").GetInt32())
                    .DefaultIfEmpty(mapped.RowEndIndex ?? 0)
                    .Max();
                mapped.StructuredRowsJson = $"[{string.Join(",", rows.Select(row => row.GetRawText()))}]";
                mapped.IsStructuredMatch = true;
                return mapped;
            })
            .OrderBy(result => result.DocumentId)
            .ThenBy(result => result.SheetName)
            .ThenBy(result => result.RowStartIndex)
            .ToList();

        return new StructuredSearchResult(true, candidates);
    }

    private static int CountMentionedHeaders(
        string query,
        IReadOnlyList<string> headers)
    {
        var normalizedQuery = $" {NormalizeStructuredValue(query)} ";
        var exactHeaderMatches = headers
            .Select(NormalizeStructuredValue)
            .Where(header => header.Length > 0 &&
                             normalizedQuery.Contains($" {header} ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Count();

        var queryTerms = GetStructuredQueryTerms(query);
        var aliasMatches = BuildHeaderAliases(headers)
            .Where(alias => GetStructuredQueryTerms(alias.Alias)
                .All(queryTerms.Contains))
            .Select(alias => NormalizeStructuredValue(alias.Header))
            .Distinct(StringComparer.Ordinal)
            .Count();

        return exactHeaderMatches * (headers.Count + 1) + aliasMatches;
    }

    private static HashSet<string> GetStructuredQueryTerms(string value)
    {
        return Regex.Matches(value, @"[\p{L}\p{N}]+")
            .Select(match => NormalizeStructuredWord(match.Value))
            .Where(word => word.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeStructuredWord(string word)
    {
        var normalized = word.ToLowerInvariant();
        if (normalized.Length > 4 &&
            normalized.EndsWith("ies", StringComparison.Ordinal))
        {
            return normalized[..^3] + "y";
        }

        if (normalized.Length > 4 &&
            normalized.EndsWith("ed", StringComparison.Ordinal))
        {
            return normalized[..^2];
        }

        if (normalized.Length > 5 &&
            normalized.EndsWith("ion", StringComparison.Ordinal))
        {
            return normalized[..^3];
        }

        if (normalized.Length > 3 &&
            normalized.EndsWith('s') &&
            !normalized.EndsWith("ss", StringComparison.Ordinal) &&
            !normalized.EndsWith("us", StringComparison.Ordinal) &&
            !normalized.EndsWith("is", StringComparison.Ordinal))
        {
            return normalized[..^1];
        }

        return normalized;
    }

    private static List<StructuredRowEntry> ApplyStructuredQueryOperation(
        string query,
        IReadOnlyList<StructuredRowEntry> sourceRows)
    {
        var rows = sourceRows
            .DistinctBy(entry =>
                $"{GetStructuredTableKey(entry.Payload)}|{entry.Row.GetRawText()}")
            .ToList();
        if (rows.Count == 0)
            return rows;

        var headers = rows
            .SelectMany(entry => entry.Row.GetProperty("fields")
                .EnumerateObject()
                .Select(field => field.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mentionedHeaders = BuildHeaderAliases(headers)
            .Where(alias => GetStructuredQueryTerms(alias.Alias)
                .All(GetStructuredQueryTerms(query).Contains))
            .Select(alias => NormalizeStructuredValue(alias.Header))
            .ToHashSet(StringComparer.Ordinal);

        if (Regex.IsMatch(
                query,
                @"\b(?:more\s+than\s+once|multiple\s+times|duplicat(?:e|es|ed|ion)|repeated)\b",
                RegexOptions.IgnoreCase))
        {
            var duplicateValues = rows
                .SelectMany(entry => entry.Row.GetProperty("fields").EnumerateObject()
                    .Where(field => mentionedHeaders.Contains(
                        NormalizeStructuredValue(field.Name)))
                    .Select(field => NormalizeStructuredValue(
                        GetStructuredScalarValue(field.Value))))
                .Where(value => value.Length > 0)
                .GroupBy(value => value, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            if (mentionedHeaders.Count > 0)
            {
                return rows
                    .Where(entry => entry.Row.GetProperty("fields")
                        .EnumerateObject()
                        .Any(field =>
                            mentionedHeaders.Contains(NormalizeStructuredValue(field.Name)) &&
                            duplicateValues.Contains(NormalizeStructuredValue(
                                GetStructuredScalarValue(field.Value)))))
                    .ToList();
            }
        }

        var numericHeaders = mentionedHeaders
            .Where(header => rows.Any(entry => entry.Row.GetProperty("fields")
                .EnumerateObject()
                .Any(field =>
                    NormalizeStructuredValue(field.Name) == header &&
                    TryParseStructuredNumber(
                        GetStructuredScalarValue(field.Value),
                        out _))))
            .ToList();

        var comparison = GetStructuredComparison(query);
        if (comparison is not null &&
            TryGetQueryNumber(query, out var threshold) &&
            numericHeaders.Count > 0)
        {
            return rows
                .Where(entry => entry.Row.GetProperty("fields")
                    .EnumerateObject()
                    .Any(field =>
                    {
                        if (!numericHeaders.Contains(
                                NormalizeStructuredValue(field.Name)) ||
                            !TryParseStructuredNumber(
                                GetStructuredScalarValue(field.Value),
                                out var actual))
                        {
                            return false;
                        }

                        return comparison switch
                        {
                            StructuredComparison.GreaterThan => actual > threshold,
                            StructuredComparison.GreaterThanOrEqual => actual >= threshold,
                            StructuredComparison.LessThan => actual < threshold,
                            StructuredComparison.LessThanOrEqual => actual <= threshold,
                            StructuredComparison.Equal => actual == threshold,
                            _ => false
                        };
                    }))
                .ToList();
        }

        var extremum = GetStructuredExtremum(query);
        if (extremum is not null && numericHeaders.Count > 0)
        {
            var values = rows
                .SelectMany(entry => entry.Row.GetProperty("fields").EnumerateObject()
                    .Where(field => numericHeaders.Contains(
                        NormalizeStructuredValue(field.Name)))
                    .Select(field => TryParseStructuredNumber(
                        GetStructuredScalarValue(field.Value),
                        out var value)
                            ? (decimal?)value
                            : null))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            if (values.Count > 0)
            {
                var target = extremum == StructuredExtremum.Maximum
                    ? values.Max()
                    : values.Min();
                return rows
                    .Where(entry => entry.Row.GetProperty("fields")
                        .EnumerateObject()
                        .Any(field =>
                            numericHeaders.Contains(
                                NormalizeStructuredValue(field.Name)) &&
                            TryParseStructuredNumber(
                                GetStructuredScalarValue(field.Value),
                                out var value) &&
                            value == target))
                    .ToList();
            }
        }

        var queriedYears = Regex.Matches(
                query,
                @"(?<![\p{L}\p{N}_-])(?:19|20)\d{2}(?![\p{L}\p{N}_-])")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (queriedYears.Count > 0 && mentionedHeaders.Count > 0)
        {
            var yearMatches = rows
                .Where(entry => entry.Row.GetProperty("fields")
                    .EnumerateObject()
                    .Any(field =>
                        mentionedHeaders.Contains(
                            NormalizeStructuredValue(field.Name)) &&
                        queriedYears.Any(year =>
                            Regex.IsMatch(
                                GetStructuredScalarValue(field.Value),
                                $@"(?<!\d){Regex.Escape(year)}(?!\d)"))))
                .ToList();
            return yearMatches;
        }

        return rows;
    }

    private static bool TryParseStructuredNumber(string value, out decimal number)
    {
        var normalized = Regex.Replace(value, @"[^\d,.\-+]", string.Empty)
            .Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(
            normalized,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static bool TryGetQueryNumber(string query, out decimal number)
    {
        var matches = Regex.Matches(
            query,
            @"(?<![\p{L}\p{N}_-])[$€£]?\s*(?<number>\d[\d,]*(?:\.\d+)?)(?![\p{L}\p{N}_-])");
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var candidate = matches[index].Groups["number"].Value;
            if (decimal.TryParse(
                    candidate.Replace(",", string.Empty, StringComparison.Ordinal),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return true;
            }
        }

        number = 0;
        return false;
    }

    private static StructuredComparison? GetStructuredComparison(string query)
    {
        if (Regex.IsMatch(query, @"\b(at\s+least|greater\s+than\s+or\s+equal|no\s+less\s+than)\b|>=", RegexOptions.IgnoreCase))
            return StructuredComparison.GreaterThanOrEqual;
        if (Regex.IsMatch(query, @"\b(at\s+most|less\s+than\s+or\s+equal|no\s+more\s+than)\b|<=", RegexOptions.IgnoreCase))
            return StructuredComparison.LessThanOrEqual;
        if (Regex.IsMatch(query, @"\b(more\s+than|greater\s+than|above|over|exceed(?:s|ing)?)\b|>", RegexOptions.IgnoreCase))
            return StructuredComparison.GreaterThan;
        if (Regex.IsMatch(query, @"\b(less\s+than|lower\s+than|below|under)\b|<", RegexOptions.IgnoreCase))
            return StructuredComparison.LessThan;
        if (Regex.IsMatch(query, @"\b(equal\s+to|exactly)\b|=", RegexOptions.IgnoreCase))
            return StructuredComparison.Equal;

        return null;
    }

    private static StructuredExtremum? GetStructuredExtremum(string query)
    {
        if (Regex.IsMatch(
                query,
                @"\b(highest|maximum|max(?:imum)?|greatest|largest|most\s+(?:expensive|costly))\b",
                RegexOptions.IgnoreCase))
        {
            return StructuredExtremum.Maximum;
        }

        if (Regex.IsMatch(
                query,
                @"\b(lowest|minimum|min(?:imum)?|smallest|least\s+(?:expensive|costly)|cheapest)\b",
                RegexOptions.IgnoreCase))
        {
            return StructuredExtremum.Minimum;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<StructuredFilter>> ExtractObservedFilters(
        string query,
        IReadOnlyDictionary<string, IReadOnlyList<StructuredRowEntry>> rowsByTable)
    {
        var normalizedQuery = $" {NormalizeStructuredValue(query)} ";
        var filtersByTable = new Dictionary<string, IReadOnlyList<StructuredFilter>>(
            StringComparer.Ordinal);
        var queryPairsByTable = rowsByTable.ToDictionary(
            table => table.Key,
            table => table.Value
                .SelectMany(entry => entry.Row.GetProperty("fields").EnumerateObject()
                    .Select(field => new
                    {
                        Header = field.Name,
                        Value = GetStructuredScalarValue(field.Value),
                        NormalizedHeader = NormalizeStructuredValue(field.Name),
                        NormalizedValue = NormalizeStructuredValue(GetStructuredScalarValue(field.Value))
                    }))
                .Where(item => item.NormalizedValue.Length >= 2 &&
                               normalizedQuery.Contains(
                                   $" {item.NormalizedValue} ",
                                   StringComparison.Ordinal))
                .DistinctBy(item =>
                    $"{item.NormalizedHeader}|{item.NormalizedValue}")
                .ToList(),
            StringComparer.Ordinal);
        var allObservedPairs = queryPairsByTable.Values
            .SelectMany(pairs => pairs)
            .ToList();
        var allHeaders = rowsByTable.Values
            .SelectMany(entries => entries)
            .SelectMany(entry => entry.Row.GetProperty("fields")
                .EnumerateObject()
                .Select(field => field.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mentionedHeaders = BuildHeaderAliases(allHeaders)
            .Where(alias => normalizedQuery.Contains(
                $" {NormalizeStructuredValue(alias.Alias)} ",
                StringComparison.Ordinal))
            .Select(alias => NormalizeStructuredValue(alias.Header))
            .ToHashSet(StringComparer.Ordinal);
        var mentionedHeaderValues = mentionedHeaders
            .Select(header => allHeaders.First(item =>
                NormalizeStructuredValue(item) == header))
            .Select(NormalizeStructuredValue)
            .ToHashSet(StringComparer.Ordinal);
        var headerByValue = allObservedPairs
            .GroupBy(item => item.NormalizedValue, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.NormalizedHeader)
                    .Distinct(StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        foreach (var (tableKey, entries) in rowsByTable)
        {
            var observedPairs = queryPairsByTable[tableKey];

            if (observedPairs.Count == 0)
                continue;

            var headers = entries
                .SelectMany(entry => entry.Row.GetProperty("fields")
                    .EnumerateObject()
                    .Select(field => field.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var matchedValues = observedPairs
                .Select(pair => pair.NormalizedValue)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var filters = new List<StructuredFilter>();
            var deferredPairs = new List<(string Value, List<string> Headers)>();

            foreach (var valueGroup in observedPairs.GroupBy(
                         pair => pair.NormalizedValue,
                         StringComparer.Ordinal))
            {
                var valueHeaders = valueGroup
                    .Select(pair => NormalizeStructuredValue(pair.Header))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (mentionedHeaderValues.Contains(valueGroup.Key) &&
                    !valueHeaders.Any(mentionedHeaders.Contains))
                {
                    continue;
                }

                var explicitHeaders = valueHeaders
                    .Where(mentionedHeaders.Contains)
                    .ToList();

                if (explicitHeaders.Count == 0 &&
                    matchedValues.Any(other =>
                        other.Length > valueGroup.Key.Length &&
                        IsContainedPhrase(valueGroup.Key, other)))
                {
                    continue;
                }

                var globallyMentionedHeaders = headerByValue[valueGroup.Key]
                    .Where(mentionedHeaders.Contains)
                    .ToList();
                var selectedHeaders = globallyMentionedHeaders.Count > 0
                    ? valueHeaders.Intersect(globallyMentionedHeaders, StringComparer.Ordinal).ToList()
                    : explicitHeaders.Count > 0
                        ? explicitHeaders
                        : valueHeaders.Count == 1 &&
                          headerByValue[valueGroup.Key].Count == 1
                            ? valueHeaders
                            : new List<string>();

                if (selectedHeaders.Count == 0 &&
                    valueHeaders.Count == 1 &&
                    !mentionedHeaderValues.Contains(valueGroup.Key))
                {
                    deferredPairs.Add((valueGroup.First().Value, valueHeaders));
                }

                foreach (var header in selectedHeaders)
                {
                    var originalHeader = headers.First(item =>
                        NormalizeStructuredValue(item) == header);
                    AddStructuredFilter(filters, originalHeader, valueGroup.First().Value);
                }
            }

            if (filters.Count > 0)
            {
                foreach (var (value, deferredHeaders) in deferredPairs)
                {
                    var header = headers.First(item =>
                        NormalizeStructuredValue(item) == deferredHeaders[0]);
                    AddStructuredFilter(filters, header, value);
                }
            }

            var distinctFilters = filters
                .DistinctBy(filter =>
                    $"{NormalizeStructuredValue(filter.Field)}|{NormalizeStructuredValue(filter.Value)}")
                .ToList();
            if (distinctFilters.Count > 0)
                filtersByTable.Add(tableKey, distinctFilters);
        }

        return filtersByTable;
    }

    private static bool IsContainedPhrase(string phrase, string largerPhrase) =>
        $" {largerPhrase} ".Contains($" {phrase} ", StringComparison.Ordinal);

    private static string GetStructuredScalarValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static HashSet<string> FindRequestedTableKeys(
        string query,
        IReadOnlyList<StructuredPayloadRows> payloadRows)
    {
        var normalizedQuery = $" {NormalizeStructuredValue(query)} ";
        var matches = payloadRows
            .SelectMany(payload => new[]
                {
                    GetPayloadValue(payload.Point, "sheetName"),
                    GetPayloadValue(payload.Point, "tableContext")
                }
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => new
                {
                    Key = GetStructuredTableKey(payload),
                    Label = NormalizeStructuredValue(label)
                }))
            .Where(item => item.Label.Length >= 3 &&
                           normalizedQuery.Contains($" {item.Label} ", StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var longestLabelLength = matches.Max(item => item.Label.Length);
        return matches
            .Where(item => item.Label.Length == longestLabelLength)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string GetStructuredTableKey(StructuredPayloadRows payload)
    {
        var tableId = GetPayloadValue(payload.Point, "tableId");
        if (!string.IsNullOrWhiteSpace(tableId))
            return tableId;

        return string.Join(
            "|",
            GetPayloadValue(payload.Point, "documentId"),
            GetPayloadValue(payload.Point, "sheetName"),
            GetPayloadValue(payload.Point, "tableContext"));
    }

    private static List<StructuredRowEntry> JoinStructuredRows(
        IReadOnlyList<StructuredRowEntry> sourceRows,
        IReadOnlyList<StructuredRowEntry> targetRows)
    {
        if (sourceRows.Count == 0 || targetRows.Count == 0)
            return new List<StructuredRowEntry>();

        var sourceFields = GetUniqueFieldValues(sourceRows);
        var targetFields = GetUniqueFieldValues(targetRows);
        var sharedJoinFields = sourceFields.Keys
            .Intersect(targetFields.Keys, StringComparer.Ordinal)
            .Where(field =>
            {
                var sourceValues = sourceFields[field];
                var targetValues = targetFields[field];
                return sourceValues.Count > 0 &&
                       targetValues.Count > 0 &&
                       sourceValues.Count == sourceRows.Count &&
                       targetValues.Count == targetRows.Count &&
                       sourceValues.Overlaps(targetValues);
            })
            .ToHashSet(StringComparer.Ordinal);

        if (sharedJoinFields.Count == 0)
            return new List<StructuredRowEntry>();

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in sourceRows)
        {
            foreach (var field in entry.Row.GetProperty("fields").EnumerateObject())
            {
                var normalizedHeader = NormalizeStructuredValue(field.Name);
                var value = NormalizeStructuredValue(GetStructuredScalarValue(field.Value));
                if (sharedJoinFields.Contains(normalizedHeader) && value.Length > 0)
                    sourceKeys.Add($"{normalizedHeader}|{value}");
            }
        }

        return targetRows
            .Where(entry => entry.Row.GetProperty("fields").EnumerateObject()
                .Any(field =>
                {
                    var normalizedHeader = NormalizeStructuredValue(field.Name);
                    var value = NormalizeStructuredValue(GetStructuredScalarValue(field.Value));
                    return sharedJoinFields.Contains(normalizedHeader) &&
                           sourceKeys.Contains($"{normalizedHeader}|{value}");
                }))
            .Select(entry => entry with { MatchesFilters = true })
            .ToList();
    }

    private static Dictionary<string, HashSet<string>> GetUniqueFieldValues(
        IReadOnlyList<StructuredRowEntry> rows)
    {
        var valuesByField = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var countsByField = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var field in row.Row.GetProperty("fields").EnumerateObject())
            {
                var header = NormalizeStructuredValue(field.Name);
                var value = NormalizeStructuredValue(GetStructuredScalarValue(field.Value));
                if (value.Length == 0)
                    continue;

                if (!valuesByField.TryGetValue(header, out var values))
                {
                    values = new HashSet<string>(StringComparer.Ordinal);
                    valuesByField.Add(header, values);
                }

                values.Add(value);
                countsByField[header] = countsByField.GetValueOrDefault(header) + 1;
            }
        }

        return valuesByField
            .Where(entry =>
                countsByField[entry.Key] == rows.Count &&
                entry.Value.Count == rows.Count)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    private static IEnumerable<string> GetStructuredRowTextParts(JsonElement row)
    {
        if (!row.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var field in fields.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.String)
            {
                yield return field.Value.GetString() ?? string.Empty;
            }
            else if (field.Value.ValueKind == JsonValueKind.Number ||
                     field.Value.ValueKind == JsonValueKind.True ||
                     field.Value.ValueKind == JsonValueKind.False)
            {
                yield return field.Value.ToString();
            }
        }

        if (row.TryGetProperty("rowText", out var rowText) &&
            rowText.ValueKind == JsonValueKind.String)
        {
            yield return rowText.GetString() ?? string.Empty;
        }

        if (row.TryGetProperty("headerText", out var headerText) &&
            headerText.ValueKind == JsonValueKind.String)
        {
            yield return headerText.GetString() ?? string.Empty;
        }
    }

    private static IEnumerable<string> GetStructuredHeaderParts(JsonElement row)
    {
        if (!row.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var field in fields.EnumerateObject())
        {
            yield return field.Name;
        }
    }

    private sealed class JsonElementRowComparer : IEqualityComparer<JsonElement>
    {
        public bool Equals(JsonElement x, JsonElement y)
        {
            return x.GetRawText().Equals(y.GetRawText(), StringComparison.Ordinal);
        }

        public int GetHashCode(JsonElement obj)
        {
            return obj.GetRawText().GetHashCode(StringComparison.Ordinal);
        }
    }

    private async Task<IReadOnlyList<StructuredPayloadRows>> LoadStructuredRowsAsync()
    {
        var points = await _payloadCache.Value;
        var payloadRows = new List<StructuredPayloadRows>();

        foreach (var point in points)
        {
            if (!point.Payload.TryGetValue("structuredRowsJson", out var jsonValue) ||
                jsonValue.KindCase != Value.KindOneofCase.StringValue ||
                string.IsNullOrWhiteSpace(jsonValue.StringValue))
            {
                continue;
            }

            using var document = JsonDocument.Parse(jsonValue.StringValue);
            var rows = document.RootElement.Clone();
            if (rows.ValueKind != JsonValueKind.Array)
                continue;

            var headers = rows.EnumerateArray()
                .Where(row => row.TryGetProperty("fields", out var fields) &&
                              fields.ValueKind == JsonValueKind.Object)
                .SelectMany(row => row.GetProperty("fields")
                    .EnumerateObject()
                    .Select(field => field.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            payloadRows.Add(new StructuredPayloadRows(point, rows, headers));
        }

        return payloadRows;
    }

    private static IReadOnlyList<(string Header, string Alias)> BuildHeaderAliases(
        IReadOnlyList<string> headers)
    {
        var wordsByHeader = headers.ToDictionary(
            header => header,
            header => Regex.Matches(header, @"[\p{L}\p{N}]+")
                .Select(match => match.Value)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        var aliasCounts = wordsByHeader.Values
            .SelectMany(words => words.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var aliases = new List<(string Header, string Alias)>();

        foreach (var (header, words) in wordsByHeader)
        {
            aliases.Add((header, header));
            foreach (var word in words.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (aliasCounts[word] == 1)
                    aliases.Add((header, word));
            }
        }

        return aliases
            .DistinctBy(item =>
                $"{NormalizeStructuredValue(item.Header)}|{NormalizeStructuredValue(item.Alias)}")
            .ToList();
    }

    private static void AddStructuredFilter(
        ICollection<StructuredFilter> filters,
        string field,
        string value)
    {
        var normalizedValue = value.Trim().Trim('"', '\'', '.', '?', '!', ';', ',');
        if (normalizedValue.Length > 0)
            filters.Add(new StructuredFilter(field, normalizedValue));
    }

    private static bool MatchesStructuredFilters(
        JsonElement row,
        IReadOnlyList<StructuredFilter> filters)
    {
        if (!row.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var filter in filters)
        {
            var matched = false;

            foreach (var field in fields.EnumerateObject())
            {
                var fieldName = NormalizeStructuredValue(field.Name);
                var expectedField = NormalizeStructuredValue(filter.Field);
                var actualValue = NormalizeStructuredValue(GetStructuredScalarValue(field.Value));
                var expectedValue = NormalizeStructuredValue(filter.Value);

                if (fieldName == expectedField && actualValue == expectedValue)
                {
                    matched = true;
                    break;
                }
            }

            if (matched)
                continue;

            return false;
        }

        return true;
    }

    private static string NormalizeStructuredValue(string value)
    {
        var trimmed = value.Trim();
        var numeric = trimmed.Replace(",", string.Empty, StringComparison.Ordinal);

        if (decimal.TryParse(
                numeric,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return number.ToString("G29", CultureInfo.InvariantCulture);
        }

        return Regex.Replace(trimmed.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ");
    }

    private async Task<IReadOnlyList<RetrievedPoint>> SearchLexicalCandidatesAsync(
        string query,
        int limit)
    {
        var terms = Tokenize(query)
            .Where(term => term.Length >= 2 || term.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (terms.Count == 0)
            return Array.Empty<RetrievedPoint>();

        var index = await _bm25Index.Value;
        if (index.Documents.Count == 0)
            return Array.Empty<RetrievedPoint>();

        var documents = terms
            .Where(index.Postings.ContainsKey)
            .SelectMany(term => index.Postings[term])
            .DistinctBy(document => document.Point.Id.Num)
            .ToList();
        if (documents.Count == 0)
            return Array.Empty<RetrievedPoint>();

        var averageLength = Math.Max(index.AverageDocumentLength, 1);
        var queryIdentifiers = ExtractQueryIdentifiers(query);
        const double k1 = 1.2;
        const double b = 0.75;

        return documents
            .Select(document =>
            {
                var score = 0d;

                foreach (var term in terms)
                {
                    if (!document.TermFrequencies.TryGetValue(term, out var frequency))
                        continue;

                    var documentFrequency =
                        index.DocumentFrequencies.GetValueOrDefault(term);
                    var inverseDocumentFrequency = Math.Log(
                        1 + (documents.Count - documentFrequency + 0.5) /
                        (documentFrequency + 0.5));
                    var normalizedFrequency =
                        frequency * (k1 + 1) /
                        (frequency + k1 *
                            (1 - b + b * document.Length / averageLength));
                    score += inverseDocumentFrequency * normalizedFrequency;
                }

                if (queryIdentifiers.Count > 0)
                {
                    if (queryIdentifiers.Any(identifier =>
                            document.Identifiers.Contains(
                                identifier,
                                StringComparer.OrdinalIgnoreCase)))
                    {
                        score += 10;
                    }
                }

                return new { document.Point, Score = score };
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Take(limit)
            .Select(candidate => candidate.Point)
            .ToList();
    }

    private async Task<Bm25Index> LoadBm25IndexAsync()
    {
        var points = await _payloadCache.Value;
        var documents = new List<Bm25Document>(points.Count);
        var documentFrequencies = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var postings = new Dictionary<string, List<Bm25Document>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var point in points)
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
            var termFrequencies = Tokenize(searchableText)
                .GroupBy(term => term, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.OrdinalIgnoreCase);
            var document = new Bm25Document(
                point,
                termFrequencies,
                termFrequencies.Values.Sum(),
                ExtractQueryIdentifiers(searchableText));
            documents.Add(document);

            foreach (var term in termFrequencies.Keys)
            {
                documentFrequencies[term] =
                    documentFrequencies.GetValueOrDefault(term) + 1;
                if (!postings.TryGetValue(term, out var termDocuments))
                {
                    termDocuments = [];
                    postings.Add(term, termDocuments);
                }
                termDocuments.Add(document);
            }
        }

        return new Bm25Index(
            documents,
            documentFrequencies,
            postings.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<Bm25Document>)entry.Value,
                StringComparer.OrdinalIgnoreCase),
            documents.Count == 0 ? 0 : documents.Average(document => document.Length));
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

        return Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+")
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

    private static string NormalizeForSearch(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
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

        return NormalizeIdentifierValue(value);
    }

    private static string NormalizeIdentifierValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        normalized = normalized.Trim('"', '\'', '`', '[', ']', '(', ')', '{', '}', '.', ',', ';', ':', '!', '?');
        normalized = normalized.Replace('_', '-');
        normalized = normalized.Replace(' ', '-');
        return normalized.ToLowerInvariant();
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

    private static bool ContainsIdentifier(
        RetrievedPoint point,
        string identifier)
    {
        var normalizedIdentifier = NormalizeForSearch(identifier);
        if (normalizedIdentifier.Length == 0)
            return false;

        var searchableText = NormalizeForSearch(string.Join(
            " ",
            GetPayloadValue(point, "documentName"),
            GetPayloadValue(point, "relativePath"),
            GetPayloadValue(point, "text"),
            GetPayloadValue(point, "parentText"),
            GetPayloadValue(point, "tableContext"),
            GetPayloadValue(point, "searchText"),
            GetPayloadValue(point, "structuredRowsJson"),
            string.Join(" ", GetPayloadStringArray(point.Payload, "rowIdentifiers"))));

        return $" {searchableText} ".Contains(
            $" {normalizedIdentifier} ",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIdentifierInMetadata(
        VectorSearchResult result,
        string identifier)
    {
        if (IsVersionIdentifier(identifier))
        {
            var metadata = $"{result.DocumentName} {result.RelativePath}";
            return ContainsVersionIdentifier(metadata, identifier);
        }

        var normalizedIdentifier = NormalizeForSearch(identifier);
        var normalizedMetadata = NormalizeForSearch(
            $"{result.DocumentName} {result.RelativePath}");

        return normalizedIdentifier.Length > 0 &&
               $" {normalizedMetadata} ".Contains(
                   $" {normalizedIdentifier} ",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIdentifierInMetadata(
        RetrievedPoint point,
        string identifier)
    {
        if (IsVersionIdentifier(identifier))
        {
            var metadata = $"{GetPayloadValue(point, "documentName")} " +
                           GetPayloadValue(point, "relativePath");
            return ContainsVersionIdentifier(metadata, identifier);
        }

        var normalizedIdentifier = NormalizeForSearch(identifier);
        var normalizedMetadata = NormalizeForSearch(
            $"{GetPayloadValue(point, "documentName")} " +
            GetPayloadValue(point, "relativePath"));

        return normalizedIdentifier.Length > 0 &&
               $" {normalizedMetadata} ".Contains(
                   $" {normalizedIdentifier} ",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersionIdentifier(string identifier) =>
        Regex.IsMatch(identifier, @"^\d+(?:\.\d+)+$");

    private static bool ContainsVersionIdentifier(string text, string identifier) =>
        Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}])(?:v(?:ersion)?\s*)?{Regex.Escape(identifier)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase);

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
            SqlObjectType = point.Payload.TryGetValue("sqlObjectType", out var sot) ? sot.StringValue : null,
            RowStartIndex = point.Payload.TryGetValue("rowStartIndex", out var rsi) ? (int?)rsi.IntegerValue : null,
            RowEndIndex = point.Payload.TryGetValue("rowEndIndex", out var rei) ? (int?)rei.IntegerValue : null,
            RowIdentifiers = GetPayloadStringArray(point.Payload, "rowIdentifiers"),
            StructuredRowsJson = GetPayloadValue(point, "structuredRowsJson")
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
            SqlObjectType = point.Payload.TryGetValue("sqlObjectType", out var sot) ? sot.StringValue : null,
            RowStartIndex = point.Payload.TryGetValue("rowStartIndex", out var rsi) ? (int?)rsi.IntegerValue : null,
            RowEndIndex = point.Payload.TryGetValue("rowEndIndex", out var rei) ? (int?)rei.IntegerValue : null,
            RowIdentifiers = GetPayloadStringArray(point.Payload, "rowIdentifiers"),
            StructuredRowsJson = GetPayloadValue(point, "structuredRowsJson")
        };
    }

    private static IReadOnlyList<string> GetPayloadStringArray(
        Google.Protobuf.Collections.MapField<string, Value> payload,
        string key)
    {
        if (!payload.TryGetValue(key, out var value) ||
            value.KindCase != Value.KindOneofCase.ListValue)
        {
            return Array.Empty<string>();
        }

        return value.ListValue.Values
            .Where(item => item.KindCase == Value.KindOneofCase.StringValue)
            .Select(item => item.StringValue)
            .ToList();
    }

    private sealed record StructuredFilter(string Field, string Value);

    private enum StructuredComparison
    {
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        Equal
    }

    private enum StructuredExtremum
    {
        Maximum,
        Minimum
    }

    private sealed record StructuredPayloadRows(
        RetrievedPoint Point,
        JsonElement Rows,
        IReadOnlyList<string> Headers);

    private sealed record StructuredRowEntry(
        StructuredPayloadRows Payload,
        JsonElement Row,
        bool MatchesFilters);

    private sealed record Bm25Document(
        RetrievedPoint Point,
        IReadOnlyDictionary<string, int> TermFrequencies,
        int Length,
        IReadOnlyList<string> Identifiers);

    private sealed record Bm25Index(
        IReadOnlyList<Bm25Document> Documents,
        IReadOnlyDictionary<string, int> DocumentFrequencies,
        IReadOnlyDictionary<string, IReadOnlyList<Bm25Document>> Postings,
        double AverageDocumentLength);

    private sealed record StructuredSearchResult(
        bool IsApplicable,
        IReadOnlyList<VectorSearchResult> Results)
    {
        public static StructuredSearchResult NotApplicable { get; } =
            new(false, Array.Empty<VectorSearchResult>());
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