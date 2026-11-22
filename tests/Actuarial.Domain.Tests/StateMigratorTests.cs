using Actuarial.Application.Seed;
using Actuarial.Application.UseCases;

namespace Actuarial.Domain.Tests;

public class StateMigratorTests
{
    [Fact]
    public void Roll_forward_grow_carries_exactly_and_lists_the_new_period()
    {
        var f = new Fixture();
        var src = f.Signed2024;
        var next = f.Draft2025;

        Assert.Equal(11, next.Structure.ExposurePeriodCount);
        Assert.Equal(src.State.Count, next.State.Count);
        Assert.Empty(next.Dormant);
        Assert.All(next.State.Values, e => Assert.Equal(ProvenanceKind.CarriedForward, e.Provenance.Kind));
        Assert.All(next.State.Values, e => Assert.Equal(src.Id, e.Provenance.From));

        var needs = StateValidator.Missing(next.State, next.Structure, next.Manifest, f.W.Library);
        Assert.Contains(Fixture.UltSel(Fixture.Year(2025)), needs);
        Assert.Contains(Fixture.Elr(Fixture.Year(2025)), needs);
        Assert.Contains(Fixture.PaidSel(new DevKey(120, 12)), needs);
        Assert.Equal(4, needs.Count);
    }

    [Fact]
    public void Slide_drops_the_oldest_period_to_dormant_and_reshape_back_restores_it()
    {
        var f = new Fixture();
        var slid = f.Svc.RollForward(f.Signed2024, new DateOnly(2026, 6, 30), GrowthMode.Slide).Analysis;

        Assert.Equal(new DateOnly(2017, 1, 1), slid.Structure.FirstExposureStart);
        Assert.Equal(10, slid.Structure.ExposurePeriodCount);
        Assert.Contains(Fixture.UltSel(Fixture.Year(2015)), slid.Dormant.Keys);
        Assert.Contains(Fixture.UltSel(Fixture.Year(2016)), slid.Dormant.Keys);
        Assert.Contains(Fixture.Elr(Fixture.Year(2015)), slid.Dormant.Keys);
        Assert.DoesNotContain(Fixture.UltSel(Fixture.Year(2015)), slid.State.Keys);

        var back = TriangleStructure.Fit(slid.ValuationDate, 12, 12, 12, 10);
        f.Svc.Reshape(slid, back);
        Assert.Contains(Fixture.UltSel(Fixture.Year(2015)), slid.State.Keys);
        Assert.Contains(Fixture.Elr(Fixture.Year(2015)), slid.State.Keys);
        Assert.Empty(slid.Dormant);
    }

    [Fact]
    public void Annual_to_quarterly_fans_out_categorical_and_parks_amounts()
    {
        var f = new Fixture();
        var a = f.Draft2025;
        var choice2016 = a.State[Fixture.UltSel(Fixture.Year(2016))].Value;

        var report = f.Svc.Reshape(a, TriangleStructure.Fit(a.ValuationDate, 3, 44, 3, 44));

        for (var q = 1; q <= 4; q++)
        {
            var entry = a.State[Fixture.UltSel(Fixture.Quarter(2016, q))];
            Assert.Equal(choice2016, entry.Value);
            Assert.Equal(ProvenanceKind.Remapped, entry.Provenance.Kind);
        }
        Assert.Contains(Fixture.Elr(Fixture.Year(2016)), a.Dormant.Keys);
        Assert.Contains("amounts", a.Dormant[Fixture.Elr(Fixture.Year(2016))].Reason);
        Assert.DoesNotContain(a.State.Keys, k => k.Table == DemoSeed.ExpectedClaims);
        Assert.NotEmpty(report.Remapped);
    }

    [Fact]
    public void Quarterly_to_annual_merges_only_unanimous_complete_periods()
    {
        var f = new Fixture();
        var a = f.Draft2025;
        f.Svc.Reshape(a, TriangleStructure.Fit(a.ValuationDate, 3, 44, 3, 44));

        // 2017: make one quarter disagree. 2018: leave all four quarters as they are (unanimous).
        f.Svc.Select(a, Fixture.UltSel(Fixture.Quarter(2017, 2)), new SelectedAverage(AverageKind.Arithmetic));
        var agreed = a.State[Fixture.UltSel(Fixture.Quarter(2018, 1))].Value;

        f.Svc.Reshape(a, TriangleStructure.Fit(a.ValuationDate, 12, 11, 12, 11));

        Assert.Equal(agreed, a.State[Fixture.UltSel(Fixture.Year(2018))].Value);
        Assert.DoesNotContain(Fixture.UltSel(Fixture.Year(2017)), a.State.Keys);
        Assert.Contains(Fixture.UltSel(Fixture.Quarter(2017, 2)), a.Dormant.Keys);
        Assert.Contains("same choice", a.Dormant[Fixture.UltSel(Fixture.Quarter(2017, 2))].Reason);
    }

    [Fact]
    public void Exact_annual_entries_return_after_a_quarterly_round_trip()
    {
        var f = new Fixture();
        var a = f.Draft2025;
        var before = a.State.ToDictionary(kv => kv.Key, kv => kv.Value.Value);

        f.Svc.Reshape(a, TriangleStructure.Fit(a.ValuationDate, 3, 44, 3, 44));
        f.Svc.Reshape(a, TriangleStructure.Fit(a.ValuationDate, 12, 11, 12, 11));

        foreach (var (k, v) in before) Assert.Equal(v, a.State[k].Value);
        Assert.Empty(a.Dormant);
    }

    [Fact]
    public void Dormant_state_returns_when_a_template_re_adds_its_table()
    {
        var f = new Fixture();
        var a = f.Draft2025;
        var count = a.State.Count;

        f.Svc.ApplyTemplate(a, DemoSeed.PaidOnly);
        Assert.Contains(a.Dormant, d => d.Key.Table == DemoSeed.Selection && d.Value.Reason == "table is not part of this analysis");
        Assert.DoesNotContain(a.State.Keys, k => k.Table == DemoSeed.Selection);
        Assert.False(a.Manifest.Pinned.ContainsKey(DemoSeed.Selection));

        f.Svc.ApplyTemplate(a, DemoSeed.Standard);
        Assert.Equal(count, a.State.Count);
        Assert.Empty(a.Dormant);
    }

    [Fact]
    public void Apply_template_keeps_pinned_versions_and_does_not_upgrade()
    {
        var f = new Fixture();
        var a = f.Draft2025;
        Assert.Equal(new TableVersion(2), f.W.Library.Latest(DemoSeed.PaidDev));
        Assert.Equal(new TableVersion(1), a.Manifest.Pinned[DemoSeed.PaidDev]);

        f.Svc.ApplyTemplate(a, DemoSeed.PaidOnly);
        f.Svc.ApplyTemplate(a, DemoSeed.Standard);

        Assert.Equal(new TableVersion(1), a.Manifest.Pinned[DemoSeed.PaidDev]);
    }

    [Fact]
    public void Upgrade_parks_selections_of_statistics_the_new_version_dropped()
    {
        var f = new Fixture();
        var source = f.Draft2025;
        var r = f.Svc.NewRevision(source, upgrade: true);

        Assert.Equal(2, r.Analysis.Revision);
        Assert.Equal(source.Id, r.Analysis.Id);
        Assert.Equal(new TableVersion(2), r.Analysis.Manifest.Pinned[DemoSeed.PaidDev]);
        var parked = r.Analysis.Dormant.Where(d => d.Value.Entry.Value is SelectedStatistic { StatisticKey: "latest" } && d.Key.Table == DemoSeed.PaidDev).ToList();
        Assert.Equal(2, parked.Count);
        Assert.All(parked, p => Assert.Contains("not offered by v2", p.Value.Reason));
        // The source revision is untouched.
        Assert.Equal(new TableVersion(1), source.Manifest.Pinned[DemoSeed.PaidDev]);
        Assert.Empty(source.Dormant);
        Assert.Throws<UseCaseException>(() => f.Svc.Select(source, Fixture.UltSel(Fixture.Year(2025)), new SelectedMethod(DemoSeed.Bf)));
    }

    [Fact]
    public void Fan_out_is_all_or_nothing_per_source()
    {
        var f = new Fixture();
        var a = f.Draft2025;
        // Dormant annual entry plus an active quarterly entry occupying one of its four targets.
        f.Svc.Reshape(a, TriangleStructure.Fit(a.ValuationDate, 3, 44, 3, 44));
        f.Svc.Select(a, Fixture.UltSel(Fixture.Quarter(2019, 3)), new SelectedAverage(AverageKind.Arithmetic));
        f.Svc.Reshape(a, TriangleStructure.Fit(a.ValuationDate, 12, 11, 12, 11));   // 2019 does not merge -> quarters dormant
        f.Svc.Select(a, Fixture.UltSel(Fixture.Year(2019)), new SelectedMethod(DemoSeed.Bf));
        f.Svc.Reshape(a, TriangleStructure.Fit(a.ValuationDate, 3, 44, 3, 44));

        // The quarters came back exactly, so the annual BF choice must not partly fan out around them.
        Assert.Equal(new SelectedAverage(AverageKind.Arithmetic), a.State[Fixture.UltSel(Fixture.Quarter(2019, 3))].Value);
        Assert.Contains(Fixture.UltSel(Fixture.Year(2019)), a.Dormant.Keys);
        Assert.DoesNotContain(a.State, s => s.Key.Exposure?.Start.Year == 2019 && s.Key.Table == DemoSeed.Selection && s.Value.Value is SelectedMethod { MethodTable: var m } && m == DemoSeed.Bf);
    }
}
