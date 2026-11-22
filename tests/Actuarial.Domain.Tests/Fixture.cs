using Actuarial.Application.Seed;
using Actuarial.Application.UseCases;

namespace Actuarial.Domain.Tests;

/// <summary>A fresh demo workspace with a fixed clock, plus shortcuts to its analyses.</summary>
internal sealed class Fixture
{
    public Workspace W { get; }
    public ActuarialService Svc { get; }

    public Fixture()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        W = DemoSeed.Build(() => t);
        Svc = new ActuarialService(W, () => t);
    }

    public Analysis Signed2024 => W.Analyses.Single(r => r.Analysis.Status == AnalysisStatus.SignedOff).Analysis;
    public Analysis Draft2025 => W.Analyses.Single(r => r.Analysis.ValuationDate == new DateOnly(2025, 12, 31)).Analysis;
    public Analysis Duplicate => W.Analyses.Single(r => r.Analysis.DerivedFrom?.Operation == Operation.Duplicate).Analysis;

    public static StateKey PaidSel(DevKey d) => new(DemoSeed.PaidDev, "selected", null, d);
    public static StateKey UltSel(ExposureKey e) => new(DemoSeed.Selection, SelectionTableDefinition.SelectedUltimate, e, null);
    public static StateKey Elr(ExposureKey e) => new(DemoSeed.ExpectedClaims, "elr", e, null);
    public static ExposureKey Year(int y) => new(new DateOnly(y, 1, 1), 12);
    public static ExposureKey Quarter(int y, int q) => new(new DateOnly(y, (q - 1) * 3 + 1, 1), 3);
}
