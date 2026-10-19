using Rag.VectorStore.Models;
using System.Text;
using System.Text.Json;

namespace Rag.VectorStore.Services;

public static class StructuredQueryAnswerFormatter
{
    public const string NotFoundAnswer = "Not found in the provided documentation.";

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

    public static string FormatAnswer(IReadOnlyList<VectorSearchResult> results)
    {
        if (TryFormatAnswer(results, out var structuredAnswer))
            return structuredAnswer;

        var passages = results
            .Select(result => new
            {
                Result = result,
                Text = string.IsNullOrWhiteSpace(result.TableId) &&
                       !string.IsNullOrWhiteSpace(result.ParentText)
                    ? result.ParentText
                    : result.Text
            })
            .Where(passage => !string.IsNullOrWhiteSpace(passage.Text))
            .OrderByDescending(passage => passage.Result.Score)
            .ThenBy(passage => passage.Result.DocumentId, StringComparer.Ordinal)
            .ThenBy(passage => passage.Result.ChunkNumber)
            .DistinctBy(passage =>
                $"{passage.Result.DocumentId}|{passage.Result.ParentBlockId}|{passage.Text}")
            .ToList();

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
