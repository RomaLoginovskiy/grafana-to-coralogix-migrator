using GrafanaToCx.Core.Converter.PanelConverters;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Coralogix rejects a metrics pie chart whose group_names is empty. When the legend format
/// does not name a label, the PromQL grouping clause does.
/// </summary>
public class PieChartGroupNamesTests
{
    [Theory]
    [InlineData("sum(sum_over_time(m[5m])) by (psw_cardtype)", "psw_cardtype")]
    [InlineData("sum by (region) (rate(m[5m]))", "region")]
    [InlineData("sum(m) BY (Region)", "Region")]
    public void GroupingLabel_IsExtracted(string promql, string expected)
    {
        Assert.Equal([expected], PieChartPanelConverter.ExtractPromqlGroupingLabels(promql));
    }

    [Fact]
    public void SeveralLabels_AreAllExtracted()
    {
        Assert.Equal(
            ["region", "tier"],
            PieChartPanelConverter.ExtractPromqlGroupingLabels("sum(m) by (region, tier)"));
    }

    [Theory]
    [InlineData("sum(amount{type=~\"a|b\"})")]
    [InlineData("")]
    [InlineData("up")]
    public void QueriesWithNoGrouping_YieldNothing(string promql)
    {
        // A pie chart over an ungrouped scalar has no slices; there is nothing to infer.
        Assert.Empty(PieChartPanelConverter.ExtractPromqlGroupingLabels(promql));
    }

    [Fact]
    public void WithoutClause_IsNotTreatedAsGrouping()
    {
        // `without` names the labels to drop, not the ones to group by.
        Assert.Empty(PieChartPanelConverter.ExtractPromqlGroupingLabels("sum(m) without (instance)"));
    }
}
