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

        // ---------- STAGE 2: SECTION SCOPING ----------

        var scope = DetectRelevantScope(queryText, candidateResults);

        if (scope == null)
        {
            scope = InferDominantScope(candidateResults);
        }

        if (scope != null)
        {
            var sectionChunks = await ResolveSectionChunksAsync(scope);
            candidateResults = MergeSectionChunks(candidateResults, sectionChunks);
        }

        var selected = SelectMinimumChunks(queryText, candidateResults, scope);

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
    // SCOPE DETECTION
    // =================================================================

    private sealed class SectionScope
    {
        public string DocumentId { get; init; } = string.Empty;
        public string SectionTitle { get; init; } = string.Empty;
        public string SectionKey { get; init; } = string.Empty;
        public string HeadingNumber { get; init; } = string.Empty;
        public int HeadingLevel { get; init; }
    }

    private sealed class HeadingInfo
    {
        public string DocumentId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Number { get; init; } = string.Empty;
        public int Level { get; init; }
        public int ChunkNumber { get; init; }
    }

    private static SectionScope? DetectRelevantScope(
    string queryText,
    IReadOnlyList<VectorSearchResult> candidates)
    {
        if (candidates.Count == 0)
            return null;

        // NEW: If the query explicitly mentions a table, or refers to a row
        // inside a table, don't scope by section — let table expansion work.
        var q = queryText.ToLowerInvariant();
        if (Regex.IsMatch(q, @"\btable\b"))
            return null;

        // NEW: If the top candidates themselves are table chunks, skip scoping.
        var topByScore = candidates
            .OrderByDescending(c => c.Score)
            .Take(5)
            .ToList();

        if (topByScore.Any(c => !string.IsNullOrWhiteSpace(c.TableId)))
            return null;

        var query = NormalizeForSearch(queryText);

        var headings = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.DocumentId))
            .SelectMany(ExtractHeadings)
            .GroupBy(h => $"{h.DocumentId}|{h.Number}|{h.Title}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (headings.Count == 0)
            return null;

        var queryTerms = Tokenize(queryText)
            .Where(t => t.Length >= 2 || t.Any(char.IsDigit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scored = headings
            .Select(h => new
            {
                Heading = h,
                Score = ScoreHeadingMatch(h.Title, queryTerms, query)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0)
            return null;

        // Require a meaningful margin over the second-best heading, otherwise
        // we're guessing — better to fall back to global ranking.
        if (scored.Count > 1 && scored[1].Score >= scored[0].Score * 0.9)
            return null;

        return BuildScope(scored[0].Heading);
    }

    private static SectionScope? InferDominantScope(
    IReadOnlyList<VectorSearchResult> candidates)
    {
        // Skip inference if the top candidate is a table chunk.
        var top = candidates
            .OrderByDescending(c => c.Score)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.DocumentId));

        if (top == null)
            return null;

        if (!string.IsNullOrWhiteSpace(top.TableId))
            return null;

        var ownHeading = ExtractHeadings(top)
            .OrderBy(h => h.Level)
            .FirstOrDefault();

        if (ownHeading != null)
            return BuildScope(ownHeading);

        var parentHeading = ExtractHeadingsFromText(
            top.ParentText, top.DocumentId, top.ChunkNumber)
            .OrderBy(h => h.Level)
            .FirstOrDefault();

        if (parentHeading != null)
            return BuildScope(parentHeading);

        return null;
    }

    private static SectionScope BuildScope(HeadingInfo heading)
    {
        return new SectionScope
        {
            DocumentId = heading.DocumentId,
            SectionTitle = heading.Title,
            SectionKey = $"{heading.DocumentId}|{heading.Number}|{heading.Title}",
            HeadingNumber = heading.Number,
            HeadingLevel = heading.Level
        };
    }

    private static IEnumerable<HeadingInfo> ExtractHeadings(
        VectorSearchResult c)
    {
        foreach (var h in ExtractHeadingsFromText(
            c.Text, c.DocumentId, c.ChunkNumber))
            yield return h;

        foreach (var h in ExtractHeadingsFromText(
            c.ParentText, c.DocumentId, c.ChunkNumber))
            yield return h;
    }

    private static IEnumerable<HeadingInfo> ExtractHeadingsFromText(
        string? text,
        string documentId,
        int chunkNumber)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        // Find "N. " or "N.M. " at a sentence boundary (start of text,
        // after newline, after ". ", "; ", "• ").
        var numberPattern = @"(?:^|[\r\n]|[\.;•\u2022]\s+)(\d+(?:\.\d+)*)\.\s+";

        foreach (RegexMatch m in Regex.Matches(text, numberPattern))
        {
            var number = m.Groups[1].Value;
            var titleStart = m.Index + m.Length;

            var remaining = text.Length - titleStart;
            if (remaining <= 0) continue;

            var length = Math.Min(200, remaining);
            var raw = text.Substring(titleStart, length);

            // Stop the title at a bullet, next numbered heading, or newline.
            var titleMatch = Regex.Match(
                raw,
                @"^(?<title>[A-Z][A-Za-z0-9 \-/&:']{1,100}?)(?=\s*[\r\n]|\s+[-•\u2022]\s|\s+\d+\.\s)");

            string title;
            if (titleMatch.Success)
            {
                title = titleMatch.Groups["title"].Value.Trim();
            }
            else
            {
                // Fallback: stop before a following capitalised word.
                var fallback = Regex.Match(
                    raw,
                    @"^[A-Z][A-Za-z0-9 \-/&:']{1,80}?(?=\s+[A-Z][a-z])");

                title = fallback.Success
                    ? fallback.Value.Trim()
                    : string.Join(" ", raw.Split(' ').Take(5));
            }

            title = title.TrimEnd('-', ' ', ':');
            if (string.IsNullOrWhiteSpace(title)) continue;

            yield return new HeadingInfo
            {
                DocumentId = documentId,
                ChunkNumber = chunkNumber,
                Number = number,
                Title = $"{number}. {title}",
                Level = number.Count(ch => ch == '.') + 1
            };
        }
    }

    private static int ScoreHeadingMatch(
        string heading,
        HashSet<string> queryTerms,
        string normalizedQuery)
    {
        var headingTerms = Tokenize(heading).ToList();
        if (headingTerms.Count == 0) return 0;

        int score = 0;
        foreach (var term in headingTerms)
        {
            if (queryTerms.Contains(term))
                score += term.Any(char.IsDigit) ? 10 : 3;
        }

        var normalizedHeading = NormalizeForSearch(heading);
        if (!string.IsNullOrWhiteSpace(normalizedHeading) &&
            normalizedQuery.Contains(normalizedHeading, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        return score;
    }

    // =================================================================
    // SECTION RESOLUTION (offset-based)
    // =================================================================

    /// <summary>
    /// Concatenates all chunks of the document, finds the section start
    /// and end offsets with regex, and maps the range back to chunks.
    /// </summary>
    private async Task<List<VectorSearchResult>> ResolveSectionChunksAsync(
        SectionScope scope)
    {
        var documentChunks = await GetDocumentChunksAsync(scope.DocumentId);

        if (documentChunks.Count == 0)
            return new List<VectorSearchResult>();

        // Concatenate all chunks with a chunk -> offset map.
        var sb = new StringBuilder();
        var chunkOffsets = new List<(int Offset, int Length, VectorSearchResult Chunk)>();

        foreach (var c in documentChunks)
        {
            var text = c.Text ?? string.Empty;
            chunkOffsets.Add((sb.Length, text.Length, c));
            sb.Append(text);
            sb.Append('\n');
        }

        var fullText = sb.ToString();

        var headingNumber = scope.HeadingNumber;
        if (string.IsNullOrWhiteSpace(headingNumber))
            return new List<VectorSearchResult>();

        // Build "N.\s+Word1\s+Word2\s+Word3" from the section title.
        var titleWords = Regex
            .Split(scope.SectionTitle ?? string.Empty, @"\s+")
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Where(w => !Regex.IsMatch(w.Trim(), @"^\d+\.?$"))
            .Select(w => w.Trim())
            .Take(3)
            .Select(Regex.Escape)
            .ToList();

        if (titleWords.Count == 0)
            return new List<VectorSearchResult>();

        var startPattern = $@"(?<!\d){Regex.Escape(headingNumber)}\.\s+" +
                           string.Join(@"\s+", titleWords);

        var startRegex = new Regex(startPattern, RegexOptions.IgnoreCase);
        var startMatch = startRegex.Match(fullText);

        if (!startMatch.Success)
            return new List<VectorSearchResult>();

        var startOffset = startMatch.Index;

        // End pattern: (top-level number + 1) followed by a capital letter.
        var topNumber = int.Parse(headingNumber.Split('.')[0]);
        var endPattern = $@"(?<![\d.]){topNumber + 1}\.\s+[A-Z]";
        var endRegex = new Regex(endPattern);

        var endMatch = endRegex.Match(fullText, startOffset + startMatch.Length);
        var endOffset = endMatch.Success ? endMatch.Index : fullText.Length;

        // Map back to chunks whose spans overlap [startOffset, endOffset).
        var selected = new List<VectorSearchResult>();

        foreach (var (offset, length, chunk) in chunkOffsets)
        {
            var chunkEnd = offset + length;

            if (chunkEnd <= startOffset) continue;
            if (offset >= endOffset) break;

            chunk.SectionTitle = scope.SectionTitle;
            chunk.SectionKey = scope.SectionKey;
            chunk.SectionStartChunk = offset;
            chunk.SectionEndChunk = endOffset;
            selected.Add(chunk);
        }

        return selected;
    }

    private static List<VectorSearchResult> MergeSectionChunks(
    IReadOnlyList<VectorSearchResult> candidates,
    IReadOnlyList<VectorSearchResult> sectionChunks)
    {
        if (sectionChunks.Count == 0)
            return candidates.ToList();

        var sectionIds = sectionChunks.Select(c => c.Id).ToHashSet();

        var sectionMeta = sectionChunks.ToDictionary(
            c => c.Id,
            c => (c.SectionTitle, c.SectionKey,
                  c.SectionStartChunk, c.SectionEndChunk));

        var merged = new Dictionary<ulong, VectorSearchResult>();

        // 1. In-scope candidates keep their scores.
        foreach (var c in candidates.Where(c => sectionIds.Contains(c.Id)))
        {
            c.Score += 200;
            if (sectionMeta.TryGetValue(c.Id, out var meta))
            {
                c.SectionTitle = meta.SectionTitle;
                c.SectionKey = meta.SectionKey;
                c.SectionStartChunk = meta.SectionStartChunk;
                c.SectionEndChunk = meta.SectionEndChunk;
            }
            merged[c.Id] = c;
        }

        // 2. In-scope chunks that weren't candidates are added.
        foreach (var s in sectionChunks)
        {
            if (!merged.ContainsKey(s.Id))
                merged[s.Id] = s;
        }

        // 3. Out-of-scope candidates are retained if any of these is true:
        //    (a) they carry an identifier match (score >= 10000)
        //    (b) they belong to a table — tables are complete logical units
        //    (c) their score is still competitive with the in-scope average
        var inScopeAvg = sectionChunks.Count > 0
            ? sectionChunks.Average(s => s.Score)
            : 0;

        foreach (var c in candidates.Where(c => !sectionIds.Contains(c.Id)))
        {
            var keep =
                c.Score >= 10000 ||
                !string.IsNullOrWhiteSpace(c.TableId) ||
                c.Score >= inScopeAvg * 0.5;

            if (keep)
                merged[c.Id] = c;
        }

        return merged.Values.ToList();
    }

    // =================================================================
    // DYNAMIC MINIMUM CHUNK SELECTION
    // =================================================================

    private static List<VectorSearchResult> SelectMinimumChunks(
        string queryText,
        IReadOnlyList<VectorSearchResult> candidates,
        SectionScope? scope)
    {
        if (candidates.Count == 0)
            return new List<VectorSearchResult>();

        var intent = DetectQueryIntent(queryText);

        // WholeSection — return every in-scope chunk (no score cutoff)
        if (intent == QueryIntent.WholeSection)
        {
            if (scope != null)
            {
                var scoped = candidates
                    .Where(c => !string.IsNullOrWhiteSpace(c.SectionKey))
                    .GroupBy(c => c.Id)
                    .Select(g => g.First())
                    .ToList();

                if (scoped.Count > 0)
                    return scoped;
            }

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

        // General intent — adaptive cutoff, prefer in-scope groups.
        const double ratio = 0.55;
        const int maxUnits = 12;

        var cutoff = Math.Max(topScore * ratio, mean + stdDev * 0.25);

        var scopedGroups = scope != null
            ? groups.Where(g => g.Chunks.Any(c =>
                !string.IsNullOrWhiteSpace(c.SectionKey))).ToList()
            : groups;

        var pool = scopedGroups.Count > 0 ? scopedGroups : groups;

        var picked = pool
            .Where(g => g.TopScore >= cutoff)
            .Take(maxUnits)
            .ToList();

        if (picked.Count == 0)
            picked = pool.Take(1).ToList();

        var result = picked
            .SelectMany(g => g.Chunks)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderByDescending(c => c.Score)
            .ToList();

        // Continuation pull for split paragraphs.
        var additional = new List<VectorSearchResult>();
        foreach (var c in result)
        {
            if (string.IsNullOrWhiteSpace(c.SectionKey)) continue;

            var next = candidates.FirstOrDefault(x =>
                x.ChunkNumber == c.ChunkNumber + 1 &&
                x.SectionKey == c.SectionKey &&
                result.All(r => r.Id != x.Id));

            if (next != null)
                additional.Add(next);
        }

        result.AddRange(additional);

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