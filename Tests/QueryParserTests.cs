using FluentAssertions;
using ReplaceValuesSql.Models;
using ReplaceValuesSql.Services;

namespace ReplaceValuesSql.Tests;

public class QueryParserTests
{
    private readonly QueryParser _parser = new();

    // ──────────────────────────── C# wrapper stripping ────────────────────────────

    [Theory]
    [InlineData("$@\"SELECT 1\"")]
    [InlineData("@\"SELECT 1\"")]
    [InlineData("$\"SELECT 1\"")]
    [InlineData("\"SELECT 1\"")]
    [InlineData("$@\"SELECT 1\";")]
    public void Parse_StripsCommonCSharpWrappers(string input)
    {
        var result = _parser.Parse(input);
        result.CleanedQuery.Should().Be("SELECT 1");
    }

    // ─────────────────────────── Cast expressions ─────────────────────────────────

    [Fact]
    public void Parse_DetectsCastExpression()
    {
        var result = _parser.Parse("SELECT {(int)SaleType.CashRegister} AS SaleType");
        var cast = result.Expressions.OfType<CastExpression>().Should().ContainSingle().Subject;
        cast.CastType.Should().Be("int");
        cast.ValuePath.Should().Be("SaleType.CashRegister");
        cast.MemberName.Should().Be("CashRegister");
    }

    [Fact]
    public void Parse_DeduplicatesCastExpressions()
    {
        var result = _parser.Parse(
            "SELECT {(int)SaleType.Cash} AS a, {(int)SaleType.Cash} AS b");
        result.Expressions.OfType<CastExpression>().Should().ContainSingle();
    }

    // ─────────────────────────── Ternary expressions ──────────────────────────────

    [Fact]
    public void Parse_DetectsSimpleTernary()
    {
        var result = _parser.Parse("{(hasIds ? \"AND Id IN @Ids\" : \"\")}");
        var ternary = result.Expressions.OfType<TernaryExpression>().Should().ContainSingle().Subject;
        ternary.TrueValue.Should().Be("AND Id IN @Ids");
        ternary.FalseValue.Should().Be("");
        ternary.ConditionVariable.Should().Be("hasIds");
        ternary.IsNegated.Should().BeFalse();
    }

    [Fact]
    public void Parse_DetectsNegatedTernary()
    {
        var result = _parser.Parse("{(!hasIds ? \"AND Type IN @Types\" : \"\")}");
        var ternary = result.Expressions.OfType<TernaryExpression>().Should().ContainSingle().Subject;
        ternary.ConditionVariable.Should().Be("hasIds");
        ternary.IsNegated.Should().BeTrue();
    }

    [Fact]
    public void Parse_GroupsTernariesByBoolVariable()
    {
        var result = _parser.Parse(
            "{(hasIds ? \"AND P.Id IN @Ids\" : \"\")}\n" +
            "{(!hasIds ? \"AND P.Type IN @Types\" : \"\")}");

        var bv = result.BoolVariables.Should().ContainSingle().Subject;
        bv.Name.Should().Be("hasIds");
        bv.Ternaries.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ComplexTernaryConditionNotGrouped()
    {
        var result = _parser.Parse("{(a && b ? \"AND x = 1\" : \"\")}");
        var ternary = result.Expressions.OfType<TernaryExpression>().Should().ContainSingle().Subject;
        ternary.ConditionVariable.Should().BeNull();
        result.BoolVariables.Should().BeEmpty();
    }

    // ─────────────────────────────── SQL parameters ───────────────────────────────

    [Fact]
    public void Parse_DetectsSimpleParam()
    {
        var result = _parser.Parse("WHERE SalonId = @SalonId");
        var param = result.Parameters.Should().ContainSingle().Subject;
        param.Name.Should().Be("SalonId");
        param.IsListParam.Should().BeFalse();
    }

    [Fact]
    public void Parse_DetectsListParam()
    {
        var result = _parser.Parse("WHERE Id IN @CompanyIds");
        var param = result.Parameters.Should().ContainSingle().Subject;
        param.Name.Should().Be("CompanyIds");
        param.IsListParam.Should().BeTrue();
    }

    [Fact]
    public void Parse_DeduplicatesParams()
    {
        var result = _parser.Parse("WHERE @SalonId = @SalonId");
        result.Parameters.Should().ContainSingle();
    }

    [Fact]
    public void Parse_IgnoresDoubleAtSystemVariables()
    {
        var result = _parser.Parse("SELECT @@ROWCOUNT, @@IDENTITY");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Parse_IgnoresParamsInsideSingleLineComments()
    {
        var result = _parser.Parse("SELECT 1 -- filter by @SalonId\nWHERE 1=1");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Parse_IgnoresParamsInsideBlockComments()
    {
        var result = _parser.Parse("SELECT 1 /* @debug param */ WHERE 1=1");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Parse_IgnoresDeclaredLocalVariables()
    {
        var result = _parser.Parse("DECLARE @RunDate DATE\nSET @RunDate = GETDATE()");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Parse_IgnoresSetLocalVariables()
    {
        var result = _parser.Parse("SET @Counter = 0\nWHERE SalonId = @SalonId");
        var param = result.Parameters.Should().ContainSingle().Subject;
        param.Name.Should().Be("SalonId");
    }

    [Fact]
    public void Parse_DetectsParamsInsideTernaryBranches()
    {
        var result = _parser.Parse("{(hasIds ? \"AND Id IN @Ids\" : \"\")}");
        result.Parameters.Should().Contain(p => p.Name == "Ids");
    }

    // ─────────────────────────────── OPENJSON ─────────────────────────────────────

    [Fact]
    public void Parse_DetectsOpenJsonParam()
    {
        var sql = """
            FROM OPENJSON(@serializedFees)
            WITH (
                [SourceFeeId] [nvarchar](50),
                [FeeInclVat] [decimal](19, 4),
                [FeeType] [smallint]
            ) X
            """;
        var result = _parser.Parse(sql);
        var param = result.Parameters.Should().ContainSingle(p => p.Name == "serializedFees").Subject;
        param.IsJsonParam.Should().BeTrue();
        param.JsonColumns.Should().HaveCount(3);
        param.JsonColumns[0].Name.Should().Be("SourceFeeId");
        param.JsonColumns[0].SqlType.Should().Be("nvarchar");
        param.JsonColumns[1].Name.Should().Be("FeeInclVat");
        param.JsonColumns[1].SqlType.Should().Be("decimal");
        param.JsonColumns[2].Name.Should().Be("FeeType");
        param.JsonColumns[2].SqlType.Should().Be("smallint");
    }

    [Fact]
    public void JsonColumn_IsNumeric_ReturnsCorrectly()
    {
        new JsonColumn { SqlType = "int"      }.IsNumeric.Should().BeTrue();
        new JsonColumn { SqlType = "decimal"  }.IsNumeric.Should().BeTrue();
        new JsonColumn { SqlType = "smallint" }.IsNumeric.Should().BeTrue();
        new JsonColumn { SqlType = "nvarchar" }.IsNumeric.Should().BeFalse();
        new JsonColumn { SqlType = "datetime" }.IsNumeric.Should().BeFalse();
    }

    // ────────────────────────────── Enum parsing ──────────────────────────────────

    [Fact]
    public void ParseEnumValues_ParsesCSharpEnumSyntax()
    {
        var text = "public enum SaleType { CashRegister = 1, OnlineBookingBundle = 2 }";
        var values = QueryParser.ParseEnumValues(text);
        values["CashRegister"].Should().Be("1");
        values["OnlineBookingBundle"].Should().Be("2");
    }

    [Fact]
    public void ParseEnumValues_ParsesFlatNameValueList()
    {
        var values = QueryParser.ParseEnumValues("CashRegister = 1\nOnlineSale = 2");
        values["CashRegister"].Should().Be("1");
        values["OnlineSale"].Should().Be("2");
    }
}
