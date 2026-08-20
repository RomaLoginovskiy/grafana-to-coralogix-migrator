using GrafanaToCx.Core.Converter;

namespace GrafanaToCx.Core.Tests;

public sealed class PromqlSeriesConsolidatorTests
{
    [Fact]
    public void Consolidate_TwoTargets_JoinsLabelReplaceCallsWithOr()
    {
        var result = PromqlSeriesConsolidator.Consolidate(
        [
            new PromqlSeriesTarget("sum(a)", "running"),
            new PromqlSeriesTarget("sum(b)", "failed")
        ]);

        Assert.NotNull(result);
        Assert.Equal(
            "label_replace(sum(a), \"series\", \"running\", \"\", \"\") or " +
            "label_replace(sum(b), \"series\", \"failed\", \"\", \"\")",
            result!.Expr);
        Assert.Equal("series", result.GroupLabel);
        Assert.Equal(2, result.SeriesCount);
    }

    /// <summary>The real `Task per status` panel — five scalar queries, legends as the only differentiator.</summary>
    [Fact]
    public void Consolidate_FiveScalarTargets_ProducesOneSlicePerQuery()
    {
        var result = PromqlSeriesConsolidator.Consolidate(
        [
            new PromqlSeriesTarget("sum(kafka_connect_worker_connector_running_task_count{job=\"$job\"})", "running"),
            new PromqlSeriesTarget("sum(kafka_connect_worker_connector_failed_task_count{job=\"$job\"})", "failed"),
            new PromqlSeriesTarget("sum(kafka_connect_worker_connector_paused_task_count{job=\"$job\"})", "paused"),
            new PromqlSeriesTarget("sum(kafka_connect_worker_connector_unassigned_task_count{job=\"$job\"})", "unassigned"),
            new PromqlSeriesTarget("sum(kafka_connect_worker_connector_destroyed_task_count{job=\"$job\"})", "destroyed")
        ]);

        Assert.NotNull(result);
        Assert.Equal(5, result!.SeriesCount);
        Assert.Equal(4, CountOccurrences(result.Expr, " or "));
        foreach (var legend in new[] { "running", "failed", "paused", "unassigned", "destroyed" })
            Assert.Contains($"\"{legend}\"", result.Expr, StringComparison.Ordinal);
    }

    [Fact]
    public void Consolidate_SingleTarget_StillStampsALabel()
    {
        var result = PromqlSeriesConsolidator.Consolidate([new PromqlSeriesTarget("sum(a)", "only")]);

        Assert.NotNull(result);
        Assert.Equal(1, result!.SeriesCount);
        Assert.DoesNotContain(" or ", result.Expr, StringComparison.Ordinal);
        Assert.Contains("label_replace(sum(a), \"series\", \"only\"", result.Expr, StringComparison.Ordinal);
    }

    [Fact]
    public void Consolidate_MissingLegend_FallsBackToMetricName()
    {
        var result = PromqlSeriesConsolidator.Consolidate(
            [new PromqlSeriesTarget("sum(node_memory_free_bytes{a=\"b\"})", null)]);

        Assert.Contains("\"node_memory_free_bytes\"", result!.Expr, StringComparison.Ordinal);
    }

    [Fact]
    public void Consolidate_NoLegendAndNoMetricSelector_FallsBackToOrdinal()
    {
        var result = PromqlSeriesConsolidator.Consolidate([new PromqlSeriesTarget("vector(1)", null)]);

        Assert.Contains("\"series 1\"", result!.Expr, StringComparison.Ordinal);
    }

    [Fact]
    public void Consolidate_LegendContainingLabelTemplate_StripsThePlaceholder()
    {
        var result = PromqlSeriesConsolidator.Consolidate(
            [new PromqlSeriesTarget("sum(a)", "Used RAM {{k8s_pod_name}}")]);

        Assert.Contains("\"Used RAM\"", result!.Expr, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", result.Expr, StringComparison.Ordinal);
    }

    /// <summary>A synthetic label must never shadow a label the query already filters on.</summary>
    [Fact]
    public void Consolidate_QueryAlreadyUsesSeriesLabel_PicksNonCollidingName()
    {
        var result = PromqlSeriesConsolidator.Consolidate(
        [
            new PromqlSeriesTarget("sum(metric{series=\"a\"})", "first"),
            new PromqlSeriesTarget("sum(metric{series=\"b\"})", "second")
        ]);

        Assert.Equal("cx_series", result!.GroupLabel);
        Assert.Contains("\"cx_series\"", result.Expr, StringComparison.Ordinal);
    }

    [Fact]
    public void Consolidate_LegendWithQuotes_IsEscaped()
    {
        var result = PromqlSeriesConsolidator.Consolidate(
            [new PromqlSeriesTarget("sum(a)", "say \"hi\"")]);

        Assert.Contains("\\\"hi\\\"", result!.Expr, StringComparison.Ordinal);
    }

    [Fact]
    public void Consolidate_BlankExpressions_AreSkipped()
    {
        var result = PromqlSeriesConsolidator.Consolidate(
        [
            new PromqlSeriesTarget("   ", "ignored"),
            new PromqlSeriesTarget("sum(a)", "kept")
        ]);

        Assert.Equal(1, result!.SeriesCount);
        Assert.DoesNotContain("ignored", result.Expr, StringComparison.Ordinal);
    }

    [Fact]
    public void Consolidate_NoUsableTargets_ReturnsNull()
    {
        Assert.Null(PromqlSeriesConsolidator.Consolidate([new PromqlSeriesTarget("", null)]));
        Assert.Null(PromqlSeriesConsolidator.Consolidate([]));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
