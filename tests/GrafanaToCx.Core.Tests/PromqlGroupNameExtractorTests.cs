using GrafanaToCx.Core.Converter;

namespace GrafanaToCx.Core.Tests;

public sealed class PromqlGroupNameExtractorTests
{
    // ── by (...) ──────────────────────────────────────────────────────────────

    [Fact]
    public void FromByClause_SingleLabel_IsExtracted()
    {
        var labels = PromqlGroupNameExtractor.FromByClause(
            "count(calls_total_total{service_name=~\"$service_name\"}) by (service_name)");

        Assert.Equal(["service_name"], labels);
    }

    [Fact]
    public void FromByClause_MultipleLabels_AreExtractedInOrder()
    {
        var labels = PromqlGroupNameExtractor.FromByClause("sum by (operation, status_code) (rate(x[5m]))");

        Assert.Equal(["operation", "status_code"], labels);
    }

    [Fact]
    public void FromByClause_NoSpaceBeforeParen_IsExtracted()
    {
        Assert.Equal(["pod"], PromqlGroupNameExtractor.FromByClause("sum by(pod) (x)"));
    }

    [Fact]
    public void FromByClause_RepeatedAcrossExpression_IsDeduplicated()
    {
        var labels = PromqlGroupNameExtractor.FromByClause(
            "avg(100 * (sum(a) by (operation) / sum(b) by (operation)))");

        Assert.Equal(["operation"], labels);
    }

    /// <summary>
    /// `without` names the labels to drop, so the surviving grouping labels depend on the data and cannot
    /// be determined statically. Treating them as group names would slice by the wrong dimension.
    /// </summary>
    [Fact]
    public void FromByClause_WithoutClause_IsIgnored()
    {
        Assert.Empty(PromqlGroupNameExtractor.FromByClause("sum without (instance) (node_cpu_seconds_total)"));
    }

    /// <summary>Metric names ending in "_By" must not be mistaken for a `by` keyword.</summary>
    [Fact]
    public void FromByClause_MetricNameEndingInBy_IsNotMatched()
    {
        Assert.Empty(PromqlGroupNameExtractor.FromByClause(
            "system_memory_usage_By{host_name=\"$host_name\",state=\"free\"}"));
    }

    [Fact]
    public void FromByClause_EmptyParens_YieldsNothing()
    {
        Assert.Empty(PromqlGroupNameExtractor.FromByClause("sum by () (x)"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromByClause_BlankInput_YieldsNothing(string? expr)
    {
        Assert.Empty(PromqlGroupNameExtractor.FromByClause(expr));
    }

    // ── legendFormat ──────────────────────────────────────────────────────────

    [Fact]
    public void FromLegendFormat_BareTemplate_IsExtracted()
    {
        Assert.Equal(["operation"], PromqlGroupNameExtractor.FromLegendFormat("{{operation}}"));
    }

    [Fact]
    public void FromLegendFormat_TemplateWithSurroundingText_IsExtracted()
    {
        Assert.Equal(["k8s_pod_name"], PromqlGroupNameExtractor.FromLegendFormat("Used RAM {{k8s_pod_name}}"));
    }

    [Fact]
    public void FromLegendFormat_InnerWhitespace_IsTrimmed()
    {
        Assert.Equal(["pod"], PromqlGroupNameExtractor.FromLegendFormat("{{ pod }}"));
    }

    [Fact]
    public void FromLegendFormat_PlainText_YieldsNothing()
    {
        Assert.Empty(PromqlGroupNameExtractor.FromLegendFormat("running"));
    }

    [Fact]
    public void FromLegendFormat_GrafanaAutoToken_YieldsNothing()
    {
        Assert.Empty(PromqlGroupNameExtractor.FromLegendFormat("__auto"));
    }

    // ── displayName ───────────────────────────────────────────────────────────

    [Fact]
    public void FromDisplayName_LabelTemplate_IsExtracted()
    {
        Assert.Equal("queue", PromqlGroupNameExtractor.FromDisplayName("${__field.labels.queue}"));
    }

    /// <summary>The original implementation required an exact match, so decorated templates were dropped.</summary>
    [Fact]
    public void FromDisplayName_TemplateWithSurroundingText_IsExtracted()
    {
        Assert.Equal("queue", PromqlGroupNameExtractor.FromDisplayName("Queue: ${__field.labels.queue} (live)"));
    }

    [Fact]
    public void FromDisplayName_PlainText_YieldsNull()
    {
        Assert.Null(PromqlGroupNameExtractor.FromDisplayName("Messages"));
    }

    [Fact]
    public void FromDisplayName_Blank_YieldsNull()
    {
        Assert.Null(PromqlGroupNameExtractor.FromDisplayName(""));
    }

    // ── label validity ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("operation", true)]
    [InlineData("k8s_pod_name", true)]
    [InlineData("_private", true)]
    [InlineData("a1", true)]
    [InlineData("1abc", false)]
    [InlineData("has space", false)]
    [InlineData("has-dash", false)]
    [InlineData("has.dot", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidLabelName_FollowsPrometheusRules(string? label, bool expected)
    {
        Assert.Equal(expected, PromqlGroupNameExtractor.IsValidLabelName(label));
    }
}
