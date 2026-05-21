namespace ReplaceValuesSql.Models;

public abstract class ParsedItem
{
    public string RawExpression { get; set; } = "";
    public string Expression { get; set; } = "";
}

public class CastExpression : ParsedItem
{
    public string CastType { get; set; } = "";
    public string ValuePath { get; set; } = "";
    public string MemberName => ValuePath.Contains('.') ? ValuePath.Split('.').Last() : ValuePath;
    public string? ReplacementValue { get; set; }
}

public class TernaryExpression : ParsedItem
{
    public string Condition { get; set; } = "";
    public string TrueValue { get; set; } = "";
    public string FalseValue { get; set; } = "";
    public bool UseTrue { get; set; } = true;
}

public class GenericExpression : ParsedItem
{
    public string? ReplacementValue { get; set; }
}

public class SqlParameter
{
    public string Name { get; set; } = "";
    public string? ReplacementValue { get; set; }
    /// <summary>True when the parameter appears after IN in the query (e.g. IN @CompanyIds).</summary>
    public bool IsListParam { get; set; }
}

public class ParseResult
{
    public string CleanedQuery { get; set; } = "";
    public List<ParsedItem> Expressions { get; set; } = new();
    public List<SqlParameter> Parameters { get; set; } = new();
}
