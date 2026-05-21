using System.Text.RegularExpressions;
using ReplaceValuesSql.Models;

namespace ReplaceValuesSql.Services;

public class QueryParser
{
    // Matches {expression} — no nested braces
    private static readonly Regex ExpressionPattern =
        new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    // Matches (type)Some.Value  — e.g. (int)SaleType.CashRegister
    private static readonly Regex CastPattern =
        new(@"^\s*\((\w+)\)([\w.]+)\s*$", RegexOptions.Compiled);

    // Matches (condition ? "trueVal" : "falseVal")
    private static readonly Regex TernaryPattern =
        new(@"^\s*\((.+?)\s*\?\s*""([^""]*)""\s*:\s*""([^""]*)""\s*\)\s*$", RegexOptions.Compiled);

    // Matches @paramName in SQL
    private static readonly Regex ParamPattern =
        new(@"@([A-Za-z_]\w*)", RegexOptions.Compiled);

    // Matches a simple boolean variable condition: optional ! then identifier
    private static readonly Regex SimpleConditionPattern =
        new(@"^(!?)([A-Za-z_]\w*)$", RegexOptions.Compiled);

    public ParseResult Parse(string rawInput)
    {
        var cleaned = CleanCSharpWrapper(rawInput);
        var expressions = ParseExpressions(cleaned);
        var boolVariables = BuildBoolVariables(expressions);
        var parameters = ParseParameters(cleaned, expressions);

        return new ParseResult
        {
            CleanedQuery = cleaned,
            Expressions = expressions,
            BoolVariables = boolVariables,
            Parameters = parameters
        };
    }

    private List<ParsedItem> ParseExpressions(string query)
    {
        var items = new List<ParsedItem>();
        var seen = new HashSet<string>();

        foreach (Match m in ExpressionPattern.Matches(query))
        {
            var inner = m.Groups[1].Value;
            if (!seen.Add(inner)) continue;

            var castMatch = CastPattern.Match(inner);
            if (castMatch.Success)
            {
                items.Add(new CastExpression
                {
                    RawExpression = m.Value,
                    Expression = inner,
                    CastType = castMatch.Groups[1].Value,
                    ValuePath = castMatch.Groups[2].Value
                });
                continue;
            }

            var ternaryMatch = TernaryPattern.Match(inner);
            if (ternaryMatch.Success)
            {
                var condition = ternaryMatch.Groups[1].Value.Trim();
                var simpleMatch = SimpleConditionPattern.Match(condition);

                var ternary = new TernaryExpression
                {
                    RawExpression = m.Value,
                    Expression = inner,
                    Condition = condition,
                    TrueValue = ternaryMatch.Groups[2].Value,
                    FalseValue = ternaryMatch.Groups[3].Value
                };

                if (simpleMatch.Success)
                {
                    ternary.IsNegated = simpleMatch.Groups[1].Value == "!";
                    ternary.ConditionVariable = simpleMatch.Groups[2].Value;
                }

                items.Add(ternary);
                continue;
            }

            items.Add(new GenericExpression
            {
                RawExpression = m.Value,
                Expression = inner
            });
        }

        return items;
    }

    private static List<BoolVariable> BuildBoolVariables(List<ParsedItem> expressions)
    {
        var dict = new Dictionary<string, BoolVariable>(StringComparer.Ordinal);

        foreach (var ternary in expressions.OfType<TernaryExpression>())
        {
            if (ternary.ConditionVariable is null) continue;

            if (!dict.TryGetValue(ternary.ConditionVariable, out var bv))
            {
                bv = new BoolVariable { Name = ternary.ConditionVariable };
                dict[ternary.ConditionVariable] = bv;
            }

            bv.Ternaries.Add(ternary);
        }

        return dict.Values.ToList();
    }

    private static readonly Regex InParamPattern =
        new(@"\bIN\s+@([A-Za-z_]\w*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches OPENJSON(@param) WITH ( — captures param name and index of the opening paren
    private static readonly Regex OpenJsonPattern =
        new(@"OPENJSON\s*\(\s*@(\w+)\s*\)\s+WITH\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches a single column definition inside WITH: [Name] [type] or [Name] type(...)
    private static readonly Regex WithColumnPattern =
        new(@"\[(\w+)\]\s+\[?(\w+)\]?(?:\s*\([^)]*\))?", RegexOptions.Compiled);

    private List<SqlParameter> ParseParameters(string query, List<ParsedItem> expressions)
    {
        // Strip {expression} blocks from the main query so we don't pick up
        // things like @"" or email-style @ tokens inside cast expressions.
        var stripped = ExpressionPattern.Replace(query, " ");

        // Add strings from ternary branches — they contain SQL fragments with @params
        var ternaryText = string.Join(" ", expressions
            .OfType<TernaryExpression>()
            .SelectMany(t => new[] { t.TrueValue, t.FalseValue }));

        var searchText = stripped + " " + ternaryText;

        // Collect which param names appear after IN
        var inListNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in InParamPattern.Matches(searchText))
            inListNames.Add(m.Groups[1].Value);

        // Collect OPENJSON params and their WITH-clause columns
        var jsonParams = new Dictionary<string, List<JsonColumn>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in OpenJsonPattern.Matches(searchText))
        {
            var name = m.Groups[1].Value;
            if (!jsonParams.ContainsKey(name))
            {
                // The opening paren of WITH ( is the last char of this match
                var columns = ParseWithColumns(searchText, m.Index + m.Length - 1);
                jsonParams[name] = columns;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = new List<SqlParameter>();

        foreach (Match m in ParamPattern.Matches(searchText))
        {
            var name = m.Groups[1].Value;
            if (seen.Add(name))
                parameters.Add(new SqlParameter
                {
                    Name = name,
                    IsListParam = inListNames.Contains(name),
                    IsJsonParam = jsonParams.ContainsKey(name),
                    JsonColumns = jsonParams.TryGetValue(name, out var cols) ? cols : new()
                });
        }

        return parameters;
    }

    /// <summary>
    /// Starting at the opening paren of WITH (, scans for the matching closing paren
    /// and extracts all [ColumnName] [SqlType] definitions within.
    /// </summary>
    private static List<JsonColumn> ParseWithColumns(string text, int openParenIndex)
    {
        int depth = 1, i = openParenIndex + 1;
        while (i < text.Length && depth > 0)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            i++;
        }
        var withContent = text.Substring(openParenIndex + 1, i - openParenIndex - 2);

        var columns = new List<JsonColumn>();
        foreach (Match m in WithColumnPattern.Matches(withContent))
            columns.Add(new JsonColumn { Name = m.Groups[1].Value, SqlType = m.Groups[2].Value });
        return columns;
    }

    /// <summary>Strips C# string prefix ($@", @", $", ") and trailing "; or " wrapper.</summary>
    private static string CleanCSharpWrapper(string input)
    {
        var s = input.Trim();

        if (s.StartsWith("$@\"")) s = s[3..];
        else if (s.StartsWith("@\"")) s = s[2..];
        else if (s.StartsWith("$\"")) s = s[2..];
        else if (s.StartsWith("\"")) s = s[1..];

        if (s.EndsWith("\";")) s = s[..^2];
        else if (s.EndsWith("\"")) s = s[..^1];

        return s;
    }

    /// <summary>
    /// Parses a pasted enum definition or Name=Value list into a lookup dictionary.
    /// Supports full C# enum syntax and simple "Name = Value" lists.
    /// </summary>
    public static Dictionary<string, string> ParseEnumValues(string enumText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Match identifier = integer (with optional negative sign)
        var pattern = new Regex(@"\b([A-Za-z_]\w*)\s*=\s*(-?\d+)", RegexOptions.Multiline);

        foreach (Match m in pattern.Matches(enumText))
            result[m.Groups[1].Value] = m.Groups[2].Value;

        return result;
    }
}
