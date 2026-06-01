using FluentAssertions;
using ReplaceValuesSql.Models;
using ReplaceValuesSql.Services;

namespace ReplaceValuesSql.Tests;

public class QueryGeneratorTests
{
    private readonly QueryGenerator _generator = new();

    private string Generate(
        string query,
        List<ParsedItem>? expressions = null,
        List<SqlParameter>? parameters = null,
        bool keepUnfilled = true)
        => _generator.Generate(query, expressions ?? [], parameters ?? [], keepUnfilled);

    // ────────────────────────────── Cast replacement ──────────────────────────────

    [Fact]
    public void Generate_ReplacesCastExpression()
    {
        var cast = new CastExpression
        {
            RawExpression = "{(int)SaleType.Cash}",
            Expression = "(int)SaleType.Cash",
            CastType = "int",
            ValuePath = "SaleType.Cash",
            ReplacementValue = "3"
        };
        var result = Generate("SELECT {(int)SaleType.Cash} AS t", [cast]);
        result.Should().Be("SELECT 3 AS t");
    }

    [Fact]
    public void Generate_KeepsCastExpressionWhenNoValueProvided()
    {
        var cast = new CastExpression
        {
            RawExpression = "{(int)SaleType.Cash}",
            Expression = "(int)SaleType.Cash",
            CastType = "int",
            ValuePath = "SaleType.Cash"
        };
        var result = Generate("SELECT {(int)SaleType.Cash} AS t", [cast]);
        result.Should().Contain("{(int)SaleType.Cash}");
    }

    // ───────────────────────────── Ternary replacement ────────────────────────────

    [Fact]
    public void Generate_TernaryUseTrueReplacesWithTrueBranch()
    {
        var ternary = MakeTernary("{(hasIds ? \"AND Id IN @Ids\" : \"\")}", "AND Id IN @Ids", "", useTrue: true);
        var result = Generate("WHERE 1=1\n{(hasIds ? \"AND Id IN @Ids\" : \"\")}", [ternary]);
        result.Should().Contain("AND Id IN @Ids");
    }

    [Fact]
    public void Generate_TernaryUseFalseReplacesWithFalseBranch()
    {
        var ternary = MakeTernary("{(hasIds ? \"AND Id IN @Ids\" : \"\")}", "AND Id IN @Ids", "", useTrue: false);
        var result = Generate("WHERE 1=1\n{(hasIds ? \"AND Id IN @Ids\" : \"\")}", [ternary]);
        result.Should().NotContain("AND Id IN @Ids");
    }

    [Fact]
    public void Generate_EmptyTernaryBranchRemovesEntireLine()
    {
        var ternary = MakeTernary("{(hasIds ? \"AND Id IN @Ids\" : \"\")}", "AND Id IN @Ids", "", useTrue: false);
        var sql = "WHERE 1=1\n    {(hasIds ? \"AND Id IN @Ids\" : \"\")}\nORDER BY Id";
        var result = Generate(sql, [ternary]);
        result.Should().NotContain("    \n");
        result.Should().Contain("WHERE 1=1");
        result.Should().Contain("ORDER BY Id");
    }

    [Fact]
    public void Generate_UnresolvedTernaryKeepsRawExpression()
    {
        var ternary = MakeTernary("{(hasIds ? \"AND Id IN @Ids\" : \"\")}", "AND Id IN @Ids", "");
        var result = Generate("WHERE {(hasIds ? \"AND Id IN @Ids\" : \"\")}", [ternary]);
        result.Should().Contain("{(hasIds ? \"AND Id IN @Ids\" : \"\")}");
    }

    // ──────────────────────────── Parameter replacement ───────────────────────────

    [Fact]
    public void Generate_ReplacesParam()
    {
        var param = new SqlParameter { Name = "SalonId", ReplacementValue = "42" };
        var result = Generate("WHERE SalonId = @SalonId", parameters: [param]);
        result.Should().Be("WHERE SalonId = 42");
    }

    [Fact]
    public void Generate_ReplacesAllOccurrencesOfParam()
    {
        var param = new SqlParameter { Name = "SalonId", ReplacementValue = "42" };
        var result = Generate("WHERE a.SalonId = @SalonId AND b.SalonId = @SalonId", parameters: [param]);
        result.Should().Be("WHERE a.SalonId = 42 AND b.SalonId = 42");
    }

    [Fact]
    public void Generate_KeepsParamWhenNoValueAndKeepUnfilled()
    {
        var param = new SqlParameter { Name = "SalonId" };
        var result = Generate("WHERE SalonId = @SalonId", parameters: [param], keepUnfilled: true);
        result.Should().Contain("@SalonId");
    }

    [Fact]
    public void Generate_RemovesParamWhenNoValueAndNotKeepUnfilled()
    {
        var param = new SqlParameter { Name = "SalonId" };
        var result = Generate("WHERE SalonId = @SalonId", parameters: [param], keepUnfilled: false);
        result.Should().NotContain("@SalonId");
    }

    [Fact]
    public void Generate_ReplacesParamCaseInsensitive()
    {
        // Param registered as "from" should replace @from, @From, @FROM, etc.
        var paramFrom = new SqlParameter { Name = "from", ReplacementValue = "'2024-01-01'" };
        var paramTo   = new SqlParameter { Name = "to",   ReplacementValue = "'2024-01-31'" };
        var result = Generate(
            "WHERE BookingDate >= @from AND BookingDate < @to AND Date <= CONVERT(DATE, @To)",
            parameters: [paramFrom, paramTo]);
        result.Should().Be("WHERE BookingDate >= '2024-01-01' AND BookingDate < '2024-01-31' AND Date <= CONVERT(DATE, '2024-01-31')");
    }

    // ─────────────────────────────── IN-list wrapping ─────────────────────────────

    [Fact]
    public void Generate_WrapsListParamWithoutParens()
    {
        var param = new SqlParameter { Name = "Ids", ReplacementValue = "1,2,3", IsListParam = true };
        var result = Generate("WHERE Id IN @Ids", parameters: [param]);
        result.Should().Contain("IN (1,2,3)");
    }

    [Fact]
    public void Generate_DoesNotDoubleWrapListParam()
    {
        var param = new SqlParameter { Name = "Ids", ReplacementValue = "(1,2,3)", IsListParam = true };
        var result = Generate("WHERE Id IN @Ids", parameters: [param]);
        result.Should().Contain("IN (1,2,3)").And.NotContain("((");
    }

    // ─────────────────────────── @@ system variables ─────────────────────────────

    [Fact]
    public void Generate_DoesNotReplaceDoubleAtVariables()
    {
        var param = new SqlParameter { Name = "ROWCOUNT", ReplacementValue = "0" };
        var result = Generate("SELECT @@ROWCOUNT", parameters: [param]);
        result.Should().Contain("@@ROWCOUNT");
    }

    // ──────────────────────── Params inside comments ──────────────────────────────

    [Fact]
    public void Generate_DoesNotReplaceParamInsideSingleLineComment()
    {
        var param = new SqlParameter { Name = "SalonId", ReplacementValue = "42" };
        var result = Generate("-- filter by @SalonId\nWHERE 1=1", parameters: [param]);
        result.Should().Contain("-- filter by @SalonId");
    }

    [Fact]
    public void Generate_DoesNotReplaceParamInsideBlockComment()
    {
        var param = new SqlParameter { Name = "SalonId", ReplacementValue = "42" };
        var result = Generate("/* @SalonId */ WHERE 1=1", parameters: [param]);
        result.Should().Contain("/* @SalonId */");
    }

    [Fact]
    public void Generate_ReplacesParamOutsideCommentButNotInside()
    {
        var param = new SqlParameter { Name = "SalonId", ReplacementValue = "42" };
        var result = Generate("-- @SalonId\nWHERE SalonId = @SalonId", parameters: [param]);
        result.Should().Contain("-- @SalonId");
        result.Should().Contain("WHERE SalonId = 42");
    }

    [Fact]
    public void Generate_QuotesValueWhenFlagSet()
    {
        var param = new SqlParameter { Name = "from", ReplacementValue = "2024-01-01", QuoteValue = true };
        var result = Generate("WHERE Date >= @from", parameters: [param]);
        result.Should().Be("WHERE Date >= '2024-01-01'");
    }

    [Fact]
    public void Generate_DoesNotQuoteValueWhenFlagNotSet()
    {
        var param = new SqlParameter { Name = "SalonId", ReplacementValue = "42", QuoteValue = false };
        var result = Generate("WHERE SalonId = @SalonId", parameters: [param]);
        result.Should().Be("WHERE SalonId = 42");
    }

    [Fact]
    public void Generate_EscapesSingleQuotesInsideQuotedValue()
    {
        var param = new SqlParameter { Name = "name", ReplacementValue = "O'Brien", QuoteValue = true };
        var result = Generate("WHERE Name = @name", parameters: [param]);
        result.Should().Be("WHERE Name = 'O''Brien'");
    }

    // ─────────────────────── JSON param with special chars ────────────────────────

    [Fact]
    public void Generate_HandlesJsonValueWithDollarSign()
    {
        var param = new SqlParameter { Name = "fees", ReplacementValue = "'[{\"name\":\"$100\"}]'" };
        var result = Generate("OPENJSON(@fees)", parameters: [param]);
        result.Should().Contain("$100");
    }

    // ──────────────────────────────── Helpers ─────────────────────────────────────

    private static TernaryExpression MakeTernary(
        string raw, string trueVal, string falseVal, bool? useTrue = null) =>
        new()
        {
            RawExpression = raw,
            Expression = raw.Trim('{', '}'),
            Condition = "hasIds",
            TrueValue = trueVal,
            FalseValue = falseVal,
            UseTrue = useTrue
        };
}
