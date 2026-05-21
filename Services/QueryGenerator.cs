using System.Text.RegularExpressions;
using ReplaceValuesSql.Models;

namespace ReplaceValuesSql.Services;

public class QueryGenerator
{
    private static readonly Regex SingleLineCommentPattern =
        new(@"--[^\n]*", RegexOptions.Compiled);
    private static readonly Regex MultiLineCommentPattern =
        new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    public string Generate(
        string cleanedQuery,
        List<ParsedItem> expressions,
        List<SqlParameter> parameters,
        bool keepUnfilledParams = true)
    {
        var result = cleanedQuery;

        // 1. Replace C# interpolation expressions
        foreach (var expr in expressions)
        {
            var replacement = expr switch
            {
                CastExpression cast when !string.IsNullOrWhiteSpace(cast.ReplacementValue)
                    => cast.ReplacementValue!,
                TernaryExpression ternary when ternary.UseTrue.HasValue
                    => ternary.UseTrue.Value ? ternary.TrueValue : ternary.FalseValue,
                GenericExpression generic when !string.IsNullOrWhiteSpace(generic.ReplacementValue)
                    => generic.ReplacementValue!,
                _ => expr.RawExpression   // keep original if no value provided
            };

            if (replacement == "")
            {
                // Expression resolves to empty — remove the whole line so we don't
                // leave behind a blank/whitespace-only line in the output.
                var linePattern = new Regex(
                    $@"(?m)^[^\S\n]*{Regex.Escape(expr.RawExpression)}[^\S\n]*(\r?\n|$)");
                result = linePattern.Replace(result, "");
            }
            else
            {
                result = result.Replace(expr.RawExpression, replacement);
            }
        }

        // 2. Replace SQL @parameters — only outside comments
        foreach (var param in parameters)
        {
            if (!string.IsNullOrWhiteSpace(param.ReplacementValue))
            {
                var value = param.ReplacementValue!.Trim();

                if (param.IsListParam && !(value.StartsWith("(") && value.EndsWith(")")))
                    value = $"({value})";

                // Replace @param only in non-comment segments
                result = ReplaceOutsideComments(result, $@"(?<!@)@{Regex.Escape(param.Name)}\b", _ => value);
            }
            else if (!keepUnfilledParams)
            {
                result = ReplaceOutsideComments(result, $@"(?<!@)@{Regex.Escape(param.Name)}\b", _ => "");
            }
        }

        // 3. Tidy up: collapse whitespace-only lines and excess blank lines
        result = Regex.Replace(result, @"(?m)^[ \t]+$", "");
        result = Regex.Replace(result, @"\n{3,}", "\n\n");

        return result.Trim();
    }

    /// <summary>
    /// Replaces <paramref name="pattern"/> matches only in non-comment segments of
    /// <paramref name="input"/>, leaving -- line comments and /* block comments */ intact.
    /// </summary>
    private static string ReplaceOutsideComments(string input, string pattern, MatchEvaluator evaluator)
    {
        // Split the text into alternating comment / non-comment segments.
        // We reassemble, only running the replacement on non-comment segments.
        var commentPattern = new Regex(@"(--[^\n]*|/\*.*?\*/)", RegexOptions.Singleline);
        var parts = commentPattern.Split(input);
        // Split returns: [non-comment, comment, non-comment, comment, ...]
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            // Even indices = non-comment text; odd indices = captured comment
            if (i % 2 == 0)
                sb.Append(Regex.Replace(parts[i], pattern, evaluator));
            else
                sb.Append(parts[i]);
        }
        return sb.ToString();
    }
}
