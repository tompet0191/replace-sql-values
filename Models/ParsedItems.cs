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

    /// <summary>
    /// null = unresolved (keep raw expression in output).
    /// true/false = which branch to use.
    /// </summary>
    public bool? UseTrue { get; set; } = null;

    /// <summary>The extracted simple variable name, if the condition is just varName or !varName.</summary>
    public string? ConditionVariable { get; set; }

    /// <summary>True when the condition is negated (!varName).</summary>
    public bool IsNegated { get; set; }
}

/// <summary>A boolean variable that controls one or more ternary expressions.</summary>
public class BoolVariable
{
    public string Name { get; set; } = "";
    /// <summary>null = not set by the user.</summary>
    public bool? Value { get; set; }
    public List<TernaryExpression> Ternaries { get; set; } = new();
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
    public List<BoolVariable> BoolVariables { get; set; } = new();
    public List<SqlParameter> Parameters { get; set; } = new();
}

