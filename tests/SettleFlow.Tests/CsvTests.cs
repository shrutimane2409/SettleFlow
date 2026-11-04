using FluentAssertions;
using SettleFlow.Api.Operations;

namespace SettleFlow.Tests;

public class CsvTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("cust_1", "\"cust_1\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("a,b", "\"a,b\"")]
    public void Values_are_quoted_and_escaped(string? input, string expected) =>
        AnalyticsEndpoints.Csv(input).Should().Be(expected);

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+cmd")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void Spreadsheet_formulas_are_neutralised(string input) =>
        AnalyticsEndpoints.Csv(input).Should().StartWith("\"'");
}
