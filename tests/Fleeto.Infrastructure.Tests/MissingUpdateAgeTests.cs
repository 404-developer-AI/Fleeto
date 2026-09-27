using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>The rule of the missing updates check (0.6.0): which updates count and how old the oldest is.</summary>
public sealed class MissingUpdateAgeTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    private static MissingUpdateAge.Update Update(string name, PatchSeverity severity, int daysAgo, string approval = "New", string kb = "") =>
        new(name, kb, severity, Today.AddDays(-daysAgo), approval);

    [Fact]
    public void The_value_is_the_age_of_the_oldest_update_that_counts()
    {
        var result = MissingUpdateAge.Evaluate(
            [Update("Chrome", PatchSeverity.Important, 20), Update("Windows", PatchSeverity.Critical, 45, kb: "KB1"), Update("Reader", PatchSeverity.Low, 3)],
            "any", Today, threshold: 14);

        Assert.Equal(45, result.Value);
        Assert.StartsWith("2 missing updates released 14 days ago or longer: Windows (KB1), released 2026-08-13; Chrome", result.Detail);
    }

    [Theory]
    [InlineData("critical", 10)]
    [InlineData("important", 20)]
    [InlineData("moderate", 30)]
    [InlineData("any", 40)]
    public void Only_updates_of_the_chosen_severity_or_worse_count(string minimum, double expected)
    {
        MissingUpdateAge.Update[] missing =
        [
            Update("Critical", PatchSeverity.Critical, 10), Update("Important", PatchSeverity.Important, 20),
            Update("Moderate", PatchSeverity.Moderate, 30), Update("Unspecified", PatchSeverity.Unspecified, 40)
        ];

        Assert.Equal(expected, MissingUpdateAge.Evaluate(missing, minimum, Today, null).Value);
    }

    [Fact]
    public void A_declined_update_and_one_without_a_release_date_do_not_count()
    {
        var result = MissingUpdateAge.Evaluate(
            [
                Update("Declined", PatchSeverity.Critical, 90, approval: "Declined"),
                new MissingUpdateAge.Update("Undated", "", PatchSeverity.Critical, null, "Approved"),
                Update("Counted", PatchSeverity.Critical, 5, approval: "Approved")
            ],
            "any", Today, threshold: 14);

        Assert.Equal(5, result.Value);
        Assert.Equal("1 missing update counted; the oldest was released 5 days ago.", result.Detail);
    }

    [Fact]
    public void Nothing_that_counts_is_zero()
    {
        Assert.Equal(0, MissingUpdateAge.Evaluate([], "any", Today, 14).Value);
        Assert.Equal(0, MissingUpdateAge.Evaluate([Update("Low", PatchSeverity.Low, 99)], "critical", Today, 14).Value);
    }

    [Fact]
    public void The_detail_names_at_most_five_updates()
    {
        var missing = Enumerable.Range(1, 8).Select(i => Update($"U{i}", PatchSeverity.Critical, 30 + i)).ToList();

        var detail = MissingUpdateAge.Evaluate(missing, "any", Today, 14).Detail;

        Assert.EndsWith("; and 3 more.", detail);
        Assert.Contains("U8", detail);
        Assert.DoesNotContain("U3,", detail);
    }

    [Fact]
    public void The_check_is_evaluated_by_Fleeto_higher_is_worse_and_has_no_interval_of_its_own()
    {
        Assert.False(CheckCatalog.Get(CheckType.MissingUpdates).RunsOnAgent);
        Assert.Equal(CheckStatus.Critical, CheckEvaluator.Evaluate(CheckType.MissingUpdates, 30, null, 14, 30));
        Assert.Equal(CheckStatus.Warning, CheckEvaluator.Evaluate(CheckType.MissingUpdates, 14, null, 14, 30));
        Assert.Equal(CheckStatus.Ok, CheckEvaluator.Evaluate(CheckType.MissingUpdates, 0, null, 14, 30));

        var definition = new CheckDefinition
        {
            Name = "Old updates", Type = CheckType.MissingUpdates, IntervalSeconds = 300, WarningThreshold = 14, CriticalThreshold = 30,
            ParametersJson = """{"severity":"important"}"""
        };
        Assert.Contains(CheckParameters.Validate(definition), p => p.Contains("no interval of its own"));
        definition.IntervalSeconds = CheckCatalog.FleetoEvaluatedIntervalSeconds;
        Assert.Empty(CheckParameters.Validate(definition));
    }
}
