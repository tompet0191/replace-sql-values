using System.Text.RegularExpressions;
using ReplaceValuesSql.Models;

namespace ReplaceValuesSql.Services;

public class QueryGenerator
{
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

        // 2. Replace SQL @parameters
        foreach (var param in parameters)
        {
            if (!string.IsNullOrWhiteSpace(param.ReplacementValue))
            {
                var value = param.ReplacementValue!.Trim();

                // Auto-wrap IN-list params: "1,2,3" → "(1,2,3)", "(1,2,3)" stays as-is
                if (param.IsListParam && !(value.StartsWith("(") && value.EndsWith(")")))
                    value = $"({value})";

                result = Regex.Replace(
                    result,
                    $@"@{Regex.Escape(param.Name)}\b",
                    value);
            }
            else if (!keepUnfilledParams)
            {
                result = Regex.Replace(result, $@"@{Regex.Escape(param.Name)}\b", "");
            }
        }

        // 3. Tidy up: collapse whitespace-only lines and excess blank lines
        result = Regex.Replace(result, @"(?m)^[ \t]+$", "");
        result = Regex.Replace(result, @"\n{3,}", "\n\n");

        return result.Trim();
    }
}
