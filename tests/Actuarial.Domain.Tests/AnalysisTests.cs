using Actuarial.Application.Persistence;
using Actuarial.Application.Seed;
using Actuarial.Application.UseCases;
using Actuarial.Domain.Formulas;

namespace Actuarial.Domain.Tests;

public class AnalysisTests
{
    [Fact]
    public void Signed_off_analysis_rejects_changes()
    {
        var f = new Fixture();
        var a = f.Signed2024;
        Assert.Throws<InvalidOperationException>(() => a.Select(Fixture.UltSel(Fixture.Year(2020)), new SelectedMethod(DemoSeed.Bf)));
        Assert.Throws<UseCaseException>(() => f.Svc.Reshape(a, a.Structure));
        Assert.Throws<UseCaseException>(() => f.Svc.ApplyTemplate(a, DemoSeed.PaidOnly));
        Assert.Throws<UseCaseException>(() => f.Svc.SignOff(a, "someone"));
    }

    [Fact]
    public void New_revision_carries_state_and_dormant_and_leaves_the_signed_one_intact()
    {
        var f = new Fixture();
        var a = f.Signed2024;
        var r = f.Svc.NewRevision(a, upgrade: false).Analysis;

        Assert.Equal(a.Id, r.Id);
        Assert.Equal(2, r.Revision);
        Assert.Equal(AnalysisStatus.Draft, r.Status);
        Assert.Equal(a.Manifest.Pinned, r.Manifest.Pinned);
        Assert.Equal(a.State.Keys.OrderBy(k => k.GetHashCode()), r.State.Keys.OrderBy(k => k.GetHashCode()));
        Assert.Equal(new Lineage(Operation.NewRevision, a.Id, 1), r.DerivedFrom);

        f.Svc.Select(r, Fixture.UltSel(Fixture.Year(2020)), new SelectedMethod(DemoSeed.Bf));
        Assert.IsType<SelectedMethod>(a.State[Fixture.UltSel(Fixture.Year(2020))].Value);
        Assert.NotEqual(new SelectedMethod(DemoSeed.Bf), a.State[Fixture.UltSel(Fixture.Year(2020))].Value);
    }

    [Fact]
    public void Values_are_validated_against_the_pinned_definition()
    {
        var f = new Fixture();
        var a = f.Draft2025;
        var age = new DevKey(12, 12);
        Assert.Throws<UseCaseException>(() => f.Svc.Select(a, Fixture.PaidSel(age), new SelectedStatistic("vwa_3")));     // only in v2
        f.Svc.Select(a, Fixture.PaidSel(age), new SelectedStatistic("latest"));                                         // in v1
        Assert.Throws<UseCaseException>(() => f.Svc.Select(a, Fixture.UltSel(Fixture.Year(2020)), new SelectedMethod(DemoSeed.Summary)));
        Assert.Throws<UseCaseException>(() => f.Svc.Select(a, new StateKey(DemoSeed.PaidDev, "cdf", null, age), new ManualValue(1.1m)));
        Assert.Throws<UseCaseException>(() => f.Svc.Select(a, new StateKey(DemoSeed.Selection, SelectionTableDefinition.SelectedUltimate, null, null), new SelectedAverage(AverageKind.Weighted)));
    }

    [Fact]
    public void Sign_off_hash_verifies_and_detects_tampering()
    {
        var f = new Fixture();
        Assert.True(f.Svc.Verify(f.Signed2024).Matches);

        // Round trip through JSON: the restored analysis still reproduces its hash.
        var back = Json.Deserialize<WorkspaceDocument>(Json.Serialize(WorkspaceDocument.From(f.W))).ToWorkspace(f.W.Warehouse);
        var svc = new ActuarialService(back);
        Assert.True(svc.Verify(back.Analyses.Single(r => r.Analysis.Status == AnalysisStatus.SignedOff).Analysis).Matches);

        // Different state => different hash.
        var draft = f.Svc.NewRevision(f.Signed2024, upgrade: false).Analysis;
        f.Svc.Select(draft, Fixture.UltSel(Fixture.Year(2020)), new SelectedMethod(DemoSeed.Bf));
        Assert.NotEqual(f.Signed2024.SignOff!.Results.Hash, f.Svc.Engine.Compute(draft, f.W.Library).Hash);
    }

    [Fact]
    public void Dormant_state_is_not_part_of_the_hash()
    {
        var f = new Fixture();
        var a = f.Draft2025;
        var before = f.Svc.Engine.Compute(a, f.W.Library).Hash;
        f.Svc.ApplyTemplate(a, DemoSeed.Standard);   // no change
        Assert.Equal(before, f.Svc.Engine.Compute(a, f.W.Library).Hash);
    }

    [Fact]
    public void Structure_validation_rejects_illegal_layouts()
    {
        var v = new DateOnly(2025, 12, 31);
        Assert.Empty(TriangleStructure.Fit(v, 12, 10, 3, 40).Validate(v));
        Assert.Contains(TriangleStructure.Fit(v, 3, 10, 12, 3).Validate(v), p => p.Contains("divide"));
        Assert.Contains(TriangleStructure.Fit(v, 12, 10, 12).Validate(new DateOnly(2025, 12, 15)), p => p.Contains("month end"));
        Assert.Contains(TriangleStructure.Fit(new DateOnly(2023, 12, 31), 12, 5, 12).Validate(v), p => p.Contains("does not reach"));
        Assert.Contains((TriangleStructure.Fit(v, 12, 10, 12) with { FirstExposureStart = new DateOnly(2016, 4, 1) }).Validate(v), p => p.Contains("boundary"));
    }

    [Fact]
    public void Duplicate_copies_onto_another_segment_as_a_draft()
    {
        var f = new Fixture();
        var d = f.Duplicate;
        Assert.Equal(AnalysisStatus.Draft, d.Status);
        Assert.Equal(f.Signed2024.ValuationDate, d.ValuationDate);
        Assert.NotEqual(f.Signed2024.Segment, d.Segment);
        Assert.NotEqual(f.Signed2024.Data.ContentHash, d.Data.ContentHash);
        Assert.Equal(f.Signed2024.State.Count, d.State.Count);
        Assert.Equal(Operation.Duplicate, d.DerivedFrom!.Operation);
    }
}

public class EngineTests
{
    [Fact]
    public void Chain_ladder_ultimate_is_latest_times_cdf()
    {
        var f = new Fixture();
        var a = f.Signed2024;
        var r = f.Svc.Engine.Compute(a, f.W.Library);
        var cl = r.Tables[DemoSeed.PaidCl];
        var dev = r.Tables[DemoSeed.PaidDev];
        var paid = r.Tables[DemoSeed.Paid].Column("paid")!;

        for (var e = 0; e < a.Structure.ExposurePeriodCount; e++)
        {
            var latestIdx = a.Structure.LatestDevIndex(a.Structure.ExposureKeys().ElementAt(e), a.ValuationDate);
            Assert.Equal(paid.At(e, latestIdx), cl.Column("latest_paid")!.At(e, 0));
            Assert.Equal(dev.Column("cdf")!.At(0, latestIdx), cl.Column("cdf")!.At(e, 0));
            Assert.Equal(cl.Column("latest_paid")!.At(e, 0) * cl.Column("cdf")!.At(e, 0), cl.Column("ultimate")!.At(e, 0));
        }
        // CDF of the last age is the tail; the oldest year develops only by the tail.
        Assert.Equal(1.012m, dev.Column("cdf")!.At(0, a.Structure.DevelopmentPeriodCount - 1));
    }

    [Fact]
    public void Volume_weighted_average_matches_hand_calculation()
    {
        var f = new Fixture();
        var a = f.Signed2024;
        var r = f.Svc.Engine.Compute(a, f.W.Library);
        var paid = r.Tables[DemoSeed.Paid].Column("paid")!;
        decimal num = 0, den = 0;
        for (var e = 0; e < 9; e++) { num += paid.At(e, 1)!.Value; den += paid.At(e, 0)!.Value; }
        Assert.Equal(num / den, r.Tables[DemoSeed.PaidDev].Column("vwa_all")!.At(0, 1));
    }

    [Fact]
    public void Selection_follows_the_chosen_method_per_period()
    {
        var f = new Fixture();
        var a = f.Signed2024;
        var r = f.Svc.Engine.Compute(a, f.W.Library);
        var sel = r.Tables[DemoSeed.Selection].Column(SelectionTableDefinition.SelectedUltimate)!;
        Assert.Equal(r.Tables[DemoSeed.PaidCl].Column("ultimate")!.At(0, 0), sel.At(0, 0));   // 2015: paid CL
        Assert.Equal(r.Tables[DemoSeed.Bf].Column("ultimate")!.At(9, 0), sel.At(9, 0));       // 2024: BF
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("-2 * -3", 6)]
    [InlineData("max(1, 4, 2) - min(5, 3)", 1)]
    public void Formula_parser_respects_precedence(string text, int expected)
    {
        var expr = Formula.Parse(text);
        decimal Eval(Expr x) => x switch
        {
            NumberExpr n => n.Value,
            NegateExpr n => -Eval(n.Operand),
            BinaryExpr b => b.Op switch { '+' => Eval(b.Left) + Eval(b.Right), '-' => Eval(b.Left) - Eval(b.Right), '*' => Eval(b.Left) * Eval(b.Right), _ => Eval(b.Left) / Eval(b.Right) },
            CallExpr c => c.Function == "max" ? c.Args.Max(Eval) : c.Args.Min(Eval),
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expected, Eval(expr));
    }
}
