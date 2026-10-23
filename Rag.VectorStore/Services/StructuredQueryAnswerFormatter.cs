using Rag.VectorStore.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rag.VectorStore.Services;

public static class StructuredQueryAnswerFormatter
{
    public const string NotFoundAnswer = "Not found in the provided documentation.";
    private static readonly HashSet<string> StructuralHeadingWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Layer", "Module", "Section", "Phase", "Stage", "Chapter", "Part",
            "Appendix", "Architecture", "Overview", "Introduction", "Requirements",
            "Specification", "Design", "Plan", "Policy", "Process", "Procedure",
            "Workflow", "Model", "Stack", "Platform", "Framework", "Summary",
            "Background", "Scope", "Purpose", "Objective", "Goal", "Approach"
        };

    public static string PreserveStructuredEvidence(
        string answer,
        IReadOnlyList<VectorSearchResult> results)
    {
        if (!answer.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            !TryFormatAnswer(results, out var structuredEvidence))
        {
            return answer;
        }

        return structuredEvidence;
    }

    public static string EnsureStructuredCompleteness(
        string query,
        string answer,
        IReadOnlyList<VectorSearchResult> results)
    {
        var rows = MergeLinkedRows(results
                .Where(result => result.IsStructuredMatch &&
                                 !string.IsNullOrWhiteSpace(result.StructuredRowsJson))
                .SelectMany(ReadRows)
                .DistinctBy(row => string.Join(
                    "\u001f",
                    row.Fields.Select(field => $"{field.Key}\u001e{field.Value}")))
                .ToList())
            .ToList();
        if (rows.Count == 0)
            return answer;

        var requestsCompleteSet = System.Text.RegularExpressions.Regex.IsMatch(
            query,
            @"\b(all|every|each|list|enumerate|more\s+than\s+once|multiple\s+times|duplicates?|repeated)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        requestsCompleteSet |= System.Text.RegularExpressions.Regex.IsMatch(
            query,
            @"\b(more\s+than|greater\s+than|above|over|at\s+least|less\s+than|lower\s+than|below|under|at\s+most)\b|>=|<=|>|<",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        requestsCompleteSet |= System.Text.RegularExpressions.Regex.IsMatch(
            query,
            @"(?<!\d)(?:19|20)\d{2}(?!\d)");
        var requestsCompleteRecord = System.Text.RegularExpressions.Regex.IsMatch(
            query,
            @"\b(full|complete|entire)\b.*\b(row|record)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var expectedIdentifiers = rows
            .SelectMany(row => row.Fields.Values)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeIdentifier)
            .ToHashSet(StringComparer.Ordinal);
        var unsupportedIdentifiers = System.Text.RegularExpressions.Regex.Matches(
                answer,
                @"\b[A-Za-z][A-Za-z0-9]*[-_][A-Za-z0-9]+\b")
            .Select(match => NormalizeIdentifier(match.Value))
            .Where(identifier => !expectedIdentifiers.Contains(identifier))
            .ToList();
        if (unsupportedIdentifiers.Count > 0)
        {
            var resultHeaders = rows
                .SelectMany(row => row.Fields.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return "Retrieved matching structured rows:" +
                   Environment.NewLine +
                   FormatTable(resultHeaders, rows);
        }

        if (!requestsCompleteSet && !requestsCompleteRecord)
            return answer;

        var uniqueValues = rows
            .SelectMany(row => row.Fields.Values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Normalize))
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Key)
            .Where(value => value.Length >= 4 ||
                            value.Any(char.IsDigit) && value.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);
        var normalizedAnswer = Normalize(answer);
        var omittedRows = rows
            .Where(row =>
            {
                var rowMarkers = row.Fields.Values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(Normalize)
                    .Where(uniqueValues.Contains)
                    .ToList();
                var represented = rowMarkers.Any(marker =>
                    ContainsWholeValue(answer, normalizedAnswer, marker));
                if (!represented)
                    return true;

                return requestsCompleteRecord &&
                       row.Fields.Values
                           .Where(value => !string.IsNullOrWhiteSpace(value))
                           .Select(Normalize)
                           .Any(value => value.Length >= 2 &&
                                         !ContainsWholeValue(answer, normalizedAnswer, value));
            })
            .ToList();
        if (omittedRows.Count == 0)
            return answer;

        var headers = omittedRows
            .SelectMany(row => row.Fields.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return $"{answer.TrimEnd()}{Environment.NewLine}{Environment.NewLine}" +
               $"Additional matching structured rows:{Environment.NewLine}" +
               FormatTable(headers, omittedRows);
    }

    public static string FormatAnswer(
        IReadOnlyList<VectorSearchResult> results,
        string? query = null)
    {
        if (TryFormatAnswer(results, out var structuredAnswer))
            return structuredAnswer;

        var passages = results
            .Select(result => new
            {
                Result = result,
                Text = result.Text
            })
            .Where(passage => !string.IsNullOrWhiteSpace(passage.Text))
            .OrderByDescending(passage => passage.Result.Score)
            .ThenBy(passage => passage.Result.DocumentId, StringComparer.Ordinal)
            .ThenBy(passage => passage.Result.ChunkNumber)
            .DistinctBy(passage =>
                $"{passage.Result.DocumentId}|{passage.Result.ParentBlockId}|{passage.Text}")
            .ToList();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var focusedPassages = passages
                .Select(passage => new
                {
                    passage.Result,
                    Text = ExtractMatchingSection(passage.Text, query) ?? string.Empty
                })
                .Where(passage => !string.IsNullOrWhiteSpace(passage.Text))
                .ToList();
            if (focusedPassages.Count > 0)
            {
                var unprunedFocusedPassages = focusedPassages;
                focusedPassages = focusedPassages
                    .Where(passage =>
                    {
                        var normalizedText = NormalizePassage(passage.Text);
                        return !unprunedFocusedPassages.Any(other =>
                        {
                            var otherText = NormalizePassage(other.Text);
                            return other.Result.DocumentId == passage.Result.DocumentId &&
                                   otherText.Length > normalizedText.Length &&
                                   otherText.Contains(
                                       normalizedText,
                                       StringComparison.Ordinal);
                        });
                    })
                    .ToList();

                var queryTerms = Regex.Matches(query, @"[\p{L}\p{N}]+")
                    .Select(match => match.Value)
                    .Where(term => term.Length > 2)
                    .ToList();
                var matchingTables = passages
                    .Where(passage => IsMarkdownTable(passage.Text) &&
                        queryTerms.Any(term => Regex.IsMatch(
                            passage.Text,
                            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
                            RegexOptions.IgnoreCase)))
                    .ToList();
                passages = focusedPassages
                    .Concat(matchingTables)
                    .DistinctBy(passage =>
                        $"{passage.Result.DocumentId}|{passage.Result.ChunkNumber}|{passage.Text}")
                    .ToList();
            }
        }

        if (passages.Count == 0)
            return NotFoundAnswer;

        var builder = new StringBuilder();
        builder.AppendLine("Retrieved source text (verbatim; not synthesized):");

        foreach (var passage in passages)
        {
            var result = passage.Result;
            builder.AppendLine();
            builder.Append("[Source: ").Append(
                string.IsNullOrWhiteSpace(result.DocumentName)
                    ? result.RelativePath
                    : result.DocumentName);

            if (result.PageNumber.HasValue)
                builder.Append(", page ").Append(result.PageNumber.Value);
            if (!string.IsNullOrWhiteSpace(result.SheetName))
                builder.Append(", sheet ").Append(result.SheetName);
            if (result.RowStartIndex.HasValue)
                builder.Append(", row ").Append(result.RowStartIndex.Value);
            if (result.RowEndIndex.HasValue &&
                result.RowEndIndex != result.RowStartIndex)
            {
                builder.Append('-').Append(result.RowEndIndex.Value);
            }

            builder.AppendLine("]");
            builder.AppendLine(passage.Text.Trim());
        }

        return builder.ToString().TrimEnd();
    }

    private static string? ExtractMatchingSection(string text, string query)
    {
        var queryTerms = Regex.Matches(query, @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var headings = Regex.Matches(
            text,
            @"(?<heading>\b(?:[\p{Lu}][\p{L}\p{N}'/&()-]*\s+){1,4}[\p{Lu}][\p{L}\p{N}'/&()-]*)(?=\s+(?:Responsible\s+for|Responsibilities|Technologies?|Technology|Description|Purpose|Scope|Overview|Requirements?|Features?|Objectives?|Goals?|Components?|Services?)\b)",
            RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match =>
            {
                var heading = match.Groups["heading"];
                var words = Regex.Matches(heading.Value, @"[\p{L}\p{N}]+")
                    .Cast<Match>()
                    .ToList();
                var structuralWordIndex = words.FindLastIndex(word =>
                    StructuralHeadingWords.Contains(word.Value));
                if (structuralWordIndex < 0)
                    return null;

                var precedingWords = words[structuralWordIndex].Value.Equals(
                    "Layer",
                    StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 2;
                var startWordIndex = Math.Max(
                    0,
                    structuralWordIndex - precedingWords);
                return new
                {
                    Match = match,
                    Start = heading.Index + words[startWordIndex].Index,
                    HeadingText = string.Join(
                        " ",
                        words.Skip(startWordIndex)
                            .Take(structuralWordIndex - startWordIndex + 1)
                            .Select(word => word.Value))
                };
            })
            .Where(heading => heading is not null)
            .Select(heading => heading!)
            .ToList();

        var matchingSections = new List<string>();
        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var headingTerms = Regex.Matches(heading.HeadingText, @"[\p{L}\p{N}]+")
                .Select(match => match.Value)
                .ToList();
            var matchingHeadingTerms = headingTerms
                .AsEnumerable()
                .Reverse()
                .TakeWhile(queryTerms.Contains)
                .Reverse()
                .ToList();
            if (matchingHeadingTerms.Count < 2)
            {
                continue;
            }

            var sectionEnd = index + 1 < headings.Count
                ? headings[index + 1].Start
                : text.Length;
            var matchingHeadingPattern = string.Join(
                @"\s+",
                matchingHeadingTerms.Select(Regex.Escape));
            var matchingHeading = Regex.Match(
                heading.HeadingText,
                matchingHeadingPattern,
                RegexOptions.IgnoreCase);
            if (!matchingHeading.Success)
                continue;

            matchingSections.Add(
                text[(heading.Start + matchingHeading.Index)..sectionEnd].Trim());
        }

        return matchingSections.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, matchingSections);
    }

    private static bool IsMarkdownTable(string text) =>
        Regex.IsMatch(text, @"(?m)^\s*\|.+\|\s*$");

    private static string NormalizePassage(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();

    public static bool TryFormatAnswer(
        IReadOnlyList<VectorSearchResult> results,
        out string answer)
    {
        answer = string.Empty;

        var rows = results
            .Where(result => result.IsStructuredMatch &&
                             !string.IsNullOrWhiteSpace(result.StructuredRowsJson))
            .SelectMany(ReadRows)
            .DistinctBy(row => string.Join(
                "\u001f",
                row.Fields.Select(field => $"{field.Key}\u001e{field.Value}")))
            .ToList();

        rows = MergeLinkedRows(rows)
            .OrderBy(row => row.RowIndex)
            .ToList();

        if (rows.Count == 0)
            return false;

        var headers = rows
            .SelectMany(row => row.Fields.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        answer = FormatTable(headers, rows);
        return true;
    }

    private static IEnumerable<StructuredRow> ReadRows(VectorSearchResult result)
    {
        using var document = JsonDocument.Parse(result.StructuredRowsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("fields", out var fields) ||
                fields.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var values = fields.EnumerateObject()
                .Where(field => field.Value.ValueKind is JsonValueKind.String or
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .ToDictionary(
                    field => field.Name,
                    field => field.Value.ValueKind == JsonValueKind.String
                        ? field.Value.GetString() ?? string.Empty
                        : field.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);

            var rowIndex = row.TryGetProperty("rowIndex", out var index) &&
                           index.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : int.MaxValue;
            yield return new StructuredRow(result.TableId, rowIndex, values);
        }
    }

    private static IReadOnlyList<StructuredRow> MergeLinkedRows(
        IReadOnlyList<StructuredRow> rows)
    {
        var parents = Enumerable.Range(0, rows.Count).ToArray();

        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
                parents[rightRoot] = leftRoot;
        }

        var valueCountsByTable = rows
            .SelectMany(row => row.Fields
                .Where(field => !string.IsNullOrWhiteSpace(field.Value))
                .Select(field => new
                {
                    row.TableId,
                    Field = Normalize(field.Key),
                    Value = Normalize(field.Value)
                }))
            .GroupBy(item => (item.TableId, item.Field, item.Value))
            .ToDictionary(group => group.Key, group => group.Count());

        for (var left = 0; left < rows.Count; left++)
        {
            for (var right = left + 1; right < rows.Count; right++)
            {
                var leftRow = rows[left];
                var rightRow = rows[right];
                if (string.IsNullOrWhiteSpace(leftRow.TableId) ||
                    string.IsNullOrWhiteSpace(rightRow.TableId) ||
                    string.Equals(leftRow.TableId, rightRow.TableId, StringComparison.Ordinal))
                {
                    continue;
                }

                var rightFields = rightRow.Fields.ToDictionary(
                    field => Normalize(field.Key),
                    field => field.Value,
                    StringComparer.Ordinal);
                var sharedFields = leftRow.Fields
                    .Where(field => rightFields.TryGetValue(Normalize(field.Key), out var value) &&
                                    !string.IsNullOrWhiteSpace(field.Value) &&
                                    !string.IsNullOrWhiteSpace(value))
                    .ToList();
                if (sharedFields.Any(field =>
                        !string.Equals(
                            Normalize(field.Value),
                            Normalize(rightFields[Normalize(field.Key)]),
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                var hasUniqueSharedValue = sharedFields.Any(field =>
                {
                    var normalizedField = Normalize(field.Key);
                    var normalizedValue = Normalize(field.Value);
                    return valueCountsByTable.GetValueOrDefault(
                               (leftRow.TableId, normalizedField, normalizedValue)) == 1 &&
                           valueCountsByTable.GetValueOrDefault(
                               (rightRow.TableId, normalizedField, normalizedValue)) == 1;
                });

                if (hasUniqueSharedValue)
                    Union(left, right);
            }
        }

        return rows
            .Select((row, index) => new { Row = row, Root = Find(index) })
            .GroupBy(item => item.Root)
            .Select(group =>
            {
                var linkedRows = group.Select(item => item.Row).ToList();
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in linkedRows)
                {
                    foreach (var (field, value) in row.Fields)
                    {
                        if (!fields.TryGetValue(field, out var existing) ||
                            string.IsNullOrWhiteSpace(existing))
                        {
                            fields[field] = value;
                        }
                    }
                }

                return new StructuredRow(
                    string.Join("|", linkedRows.Select(row => row.TableId).Distinct()),
                    linkedRows.Min(row => row.RowIndex),
                    fields);
            })
            .ToList();
    }

    private static string Normalize(string value) =>
        string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static bool ContainsWholeValue(
        string answer,
        string normalizedAnswer,
        string normalizedValue)
    {
        if (decimal.TryParse(
                normalizedValue,
                System.Globalization.NumberStyles.Currency,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                answer,
                $@"(?<![\p{{L}}\p{{N}}.,]){System.Text.RegularExpressions.Regex.Escape(normalizedValue)}(?![\p{{L}}\p{{N}}.,])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        var normalizedText = System.Text.RegularExpressions.Regex.Replace(
                normalizedValue,
                @"[^\p{L}\p{N}]+",
                " ")
            .Trim();
        var normalizedAnswerText = System.Text.RegularExpressions.Regex.Replace(
                normalizedAnswer,
                @"[^\p{L}\p{N}]+",
                " ")
            .Trim();
        return normalizedText.Length > 0 &&
               $" {normalizedAnswerText} ".Contains(
                   $" {normalizedText} ",
                   StringComparison.Ordinal);
    }

    private static string NormalizeIdentifier(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
                value,
                @"[^\p{L}\p{N}]+",
                " ")
            .Trim()
            .ToLowerInvariant();

    private static string FormatTable(
        IReadOnlyList<string> headers,
        IReadOnlyList<StructuredRow> rows)
    {
        var builder = new StringBuilder();
        builder.Append("| ").Append(string.Join(" | ", headers)).AppendLine(" |");
        builder.Append("| ")
            .Append(string.Join(" | ", headers.Select(_ => "---")))
            .AppendLine(" |");

        foreach (var row in rows)
        {
            builder.Append("| ")
                .Append(string.Join(" | ", headers.Select(header =>
                    EscapeTableValue(row.Fields.GetValueOrDefault(header, string.Empty)))))
                .AppendLine(" |");
        }

        return builder.ToString().TrimEnd();
    }

    private static string EscapeTableValue(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private sealed record StructuredRow(
        string TableId,
        int RowIndex,
        IReadOnlyDictionary<string, string> Fields);
}
