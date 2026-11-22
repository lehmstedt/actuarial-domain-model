using System.Security.Cryptography;
using System.Text;
using Actuarial.Application.Data;
using Actuarial.Application.UseCases;
using Actuarial.Domain;

namespace Actuarial.Application.Seed;

/// <summary>Stable ids derived from names, so a reset workspace has the same ids every time.</summary>
public static class Ids
{
    public static Guid Of(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("actuarial-demo:" + name)).AsSpan(0, 16));
    public static TableId Table(string name) => new(Of("table:" + name));
    public static TemplateId Template(string name) => new(Of("template:" + name));
    public static SegmentId Segment(string name) => new(Of("segment:" + name));
}

/// <summary>
/// A small but complete reserving library, two templates, four segments and a story:
/// a signed-off 2024 analysis, its 2025 roll-forward draft, a duplicate onto another segment,
/// and a later library change that makes an upgrade available.
/// </summary>
public static class DemoSeed
{
    public static readonly TableId Paid = Ids.Table("paid");
    public static readonly TableId Incurred = Ids.Table("incurred");
    public static readonly TableId Premium = Ids.Table("premium");
    public static readonly TableId CaseReserves = Ids.Table("case");
    public static readonly TableId PaidDev = Ids.Table("paid-dev");
    public static readonly TableId IncurredDev = Ids.Table("incurred-dev");
    public static readonly TableId PaidCl = Ids.Table("paid-cl");
    public static readonly TableId IncurredCl = Ids.Table("incurred-cl");
    public static readonly TableId LossRatios = Ids.Table("lr-exhibit");
    public static readonly TableId ExpectedClaims = Ids.Table("expected");
    public static readonly TableId Bf = Ids.Table("bf");
    public static readonly TableId Selection = Ids.Table("selection");
    public static readonly TableId Summary = Ids.Table("summary");

    public static readonly TemplateId Standard = Ids.Template("standard");
    public static readonly TemplateId PaidOnly = Ids.Template("paid-only");

    private static readonly TableVersion V1 = new(1);

    private static InputRequirement Req(TableId t, string col, GrainKind g, MeasureKind? m = null, BasisKind? b = null) =>
        new(TableRef.Floating(t), col, g, m, b);

    private static MethodColumn F(string key, string label, string expr, MeasureKind m, params InputRequirement[] refs) =>
        new(key, label, new FormulaColumn(expr, refs), m);

    public static IReadOnlyList<StatisticSpec> PaidStatisticsV1 => new StatisticSpec[]
    {
        new("vwa_all", StatisticKind.VolumeWeightedAverage, null),
        new("vwa_5", StatisticKind.VolumeWeightedAverage, 5),
        new("sa_3", StatisticKind.SimpleAverage, 3),
        new("medial_5", StatisticKind.Medial, 5),
        new("latest", StatisticKind.Latest, null),
    };

    public static IReadOnlyList<TableDefinition> LibraryV1()
    {
        var paidCell = Req(Paid, "paid", GrainKind.DevelopmentCell, MeasureKind.Paid, BasisKind.Cumulative);
        var incCell = Req(Incurred, "incurred", GrainKind.DevelopmentCell, MeasureKind.Incurred, BasisKind.Cumulative);
        var prem = Req(Premium, "premium", GrainKind.ExposurePeriod, MeasureKind.Premium);

        return new TableDefinition[]
        {
            new InputTableDefinition(Paid, V1, "Paid losses", new DataSource("paid", "Paid"),
                new TableSchema(new[] { new ColumnSchema("paid", MeasureKind.Paid, BasisKind.Cumulative, GrainKind.DevelopmentCell) })),
            new InputTableDefinition(Incurred, V1, "Incurred losses", new DataSource("incurred", "Incurred"),
                new TableSchema(new[] { new ColumnSchema("incurred", MeasureKind.Incurred, BasisKind.Cumulative, GrainKind.DevelopmentCell) })),
            new InputTableDefinition(Premium, V1, "Earned premium", new DataSource("premium", "Premium"),
                new TableSchema(new[] { new ColumnSchema("premium", MeasureKind.Premium, BasisKind.NotApplicable, GrainKind.ExposurePeriod) })),
            new InputTableDefinition(CaseReserves, V1, "Case reserves", new FormulaSource("$1 - $2", new[] { incCell, paidCell }),
                new TableSchema(new[] { new ColumnSchema("case", MeasureKind.Other, BasisKind.Cumulative, GrainKind.DevelopmentCell) })),

            new DevelopmentExhibitDefinition(PaidDev, V1, "Paid development", TableRef.Floating(Paid), "paid", PaidStatisticsV1, IncludeTailFactor: true),
            new DevelopmentExhibitDefinition(IncurredDev, V1, "Incurred development", TableRef.Floating(Incurred), "incurred", new StatisticSpec[]
            {
                new("vwa_all", StatisticKind.VolumeWeightedAverage, null),
                new("vwa_5", StatisticKind.VolumeWeightedAverage, 5),
                new("sa_5", StatisticKind.SimpleAverage, 5),
                new("latest", StatisticKind.Latest, null),
            }, IncludeTailFactor: true),

            new MethodTableDefinition(PaidCl, V1, "Paid chain ladder", new[]
            {
                F("premium", "Premium", "$1", MeasureKind.Premium, prem),
                F("latest_paid", "Paid to date", "latest($1)", MeasureKind.Paid, paidCell),
                F("cdf", "Paid CDF", "current($1)", MeasureKind.Factor, Req(PaidDev, "cdf", GrainKind.DevelopmentPeriod, MeasureKind.Factor)),
                F("ultimate", "Ultimate", "latest_paid * cdf", MeasureKind.Ultimate),
                F("unpaid", "Unpaid", "ultimate - latest_paid", MeasureKind.Other),
                F("loss_ratio", "Loss ratio", "ultimate / premium", MeasureKind.Other),
            }, "ultimate"),

            new MethodTableDefinition(IncurredCl, V1, "Incurred chain ladder", new[]
            {
                F("premium", "Premium", "$1", MeasureKind.Premium, prem),
                F("latest_incurred", "Incurred to date", "latest($1)", MeasureKind.Incurred, incCell),
                F("cdf", "Incurred CDF", "current($1)", MeasureKind.Factor, Req(IncurredDev, "cdf", GrainKind.DevelopmentPeriod, MeasureKind.Factor)),
                F("ultimate", "Ultimate", "latest_incurred * cdf", MeasureKind.Ultimate),
                F("ibnr", "IBNR", "ultimate - latest_incurred", MeasureKind.Other),
                F("loss_ratio", "Loss ratio", "ultimate / premium", MeasureKind.Other),
            }, "ultimate"),

            new ExhibitTableDefinition(LossRatios, V1, "Loss ratio exhibit",
                new[] { Req(IncurredCl, "loss_ratio", GrainKind.ExposurePeriod), prem },
                new StatisticSpec[]
                {
                    new("sa_all", StatisticKind.SimpleAverage, null),
                    new("wa_all", StatisticKind.VolumeWeightedAverage, null),
                    new("wa_5", StatisticKind.VolumeWeightedAverage, 5),
                    new("medial_all", StatisticKind.Medial, null),
                    new("judgement", StatisticKind.Custom, null),
                },
                new TableSchema(new[] { "sa_all", "wa_all", "wa_5", "medial_all", "judgement", "selected" }
                    .Select(k => new ColumnSchema(k, MeasureKind.Other, BasisKind.NotApplicable, GrainKind.Scalar)).ToList())),

            new MethodTableDefinition(ExpectedClaims, V1, "Expected claims", new[]
            {
                F("premium", "Premium", "$1", MeasureKind.Premium, prem),
                new MethodColumn("elr", "A priori loss ratio", new UserInputColumn(), MeasureKind.Other),
                F("ultimate", "Ultimate", "premium * elr", MeasureKind.Ultimate),
            }, "ultimate"),

            new MethodTableDefinition(Bf, V1, "Bornhuetter-Ferguson", new[]
            {
                F("premium", "Premium", "$1", MeasureKind.Premium, prem),
                F("elr", "Expected loss ratio", "$1", MeasureKind.Other, Req(LossRatios, "selected", GrainKind.Scalar)),
                F("latest_paid", "Paid to date", "latest($1)", MeasureKind.Paid, paidCell),
                F("cdf", "Paid CDF", "current($1)", MeasureKind.Factor, Req(PaidDev, "cdf", GrainKind.DevelopmentPeriod, MeasureKind.Factor)),
                F("unreported", "Expected unpaid", "premium * elr * (1 - 1 / cdf)", MeasureKind.Other),
                F("ultimate", "Ultimate", "latest_paid + unreported", MeasureKind.Ultimate),
                F("loss_ratio", "Loss ratio", "ultimate / premium", MeasureKind.Other),
            }, "ultimate"),

            new SelectionTableDefinition(Selection, V1, "Ultimate selection",
                new[] { prem },
                new[] { AverageKind.Arithmetic, AverageKind.Weighted },
                new[] { PaidCl, IncurredCl, Bf, ExpectedClaims }.Select(t => new MethodRef(TableRef.Floating(t), "ultimate")).ToList()),

            new MethodTableDefinition(Summary, V1, "Reserve summary", new[]
            {
                F("premium", "Premium", "$1", MeasureKind.Premium, prem),
                F("selected", "Selected ultimate", "$1", MeasureKind.Ultimate, Req(Selection, "selected_ultimate", GrainKind.ExposurePeriod, MeasureKind.Ultimate)),
                F("paid", "Paid to date", "latest($1)", MeasureKind.Paid, paidCell),
                F("incurred", "Incurred to date", "latest($1)", MeasureKind.Incurred, incCell),
                F("case", "Case reserves", "incurred - paid", MeasureKind.Other),
                F("ibnr", "IBNR", "selected - incurred", MeasureKind.Other),
                F("reserve", "Total reserve", "selected - paid", MeasureKind.Other),
                F("loss_ratio", "Loss ratio", "selected / premium", MeasureKind.Other),
            }, "selected"),
        };
    }

    private static ObjectNode Obj(string name, TableId t) => new(Ids.Of("node:" + name + t.Value), name, TableRef.Floating(t));
    private static CollectionNode Col(string name, params ObjectNode[] objs) => new(Ids.Of("col:" + name), name, objs);
    private static FolderNode Folder(string name, params TemplateNode[] children) => FolderNode.Create(Ids.Of("folder:" + name), name, children);

    public static AnalysisTemplate StandardTemplate() => new(Standard, new TemplateVersion(1), "Standard reserving",
        Folder("Standard reserving",
            Folder("Data",
                Col("Triangles", Obj("Paid losses", Paid), Obj("Incurred losses", Incurred), Obj("Case reserves", CaseReserves)),
                Col("Exposure", Obj("Earned premium", Premium))),
            Folder("Development",
                Col("Factors", Obj("Paid development", PaidDev), Obj("Incurred development", IncurredDev))),
            Folder("Methods",
                Col("Chain ladder", Obj("Paid chain ladder", PaidCl), Obj("Incurred chain ladder", IncurredCl)),
                Col("Expected loss", Obj("Loss ratio exhibit", LossRatios), Obj("Expected claims", ExpectedClaims), Obj("Bornhuetter-Ferguson", Bf))),
            Folder("Results",
                Col("Selection", Obj("Ultimate selection", Selection), Obj("Reserve summary", Summary)),
                Col("Review", Obj("Paid development (review)", PaidDev), Obj("Ultimate selection (review)", Selection)))));

    public static AnalysisTemplate PaidOnlyTemplate() => new(PaidOnly, new TemplateVersion(1), "Paid only",
        Folder("Paid only",
            Folder("Data", Col("Inputs", Obj("Paid losses", Paid), Obj("Earned premium", Premium))),
            Folder("Analysis", Col("Chain ladder", Obj("Paid development", PaidDev), Obj("Paid chain ladder", PaidCl)))));

    /// <summary>Paid development v2: drops "latest" and adds a three-year volume-weighted average.</summary>
    public static TableDefinition PaidDevV2() =>
        new DevelopmentExhibitDefinition(PaidDev, new TableVersion(2), "Paid development", TableRef.Floating(Paid), "paid",
            PaidStatisticsV1.Where(s => s.Key != "latest").Append(new StatisticSpec("vwa_3", StatisticKind.VolumeWeightedAverage, 3)).ToList(),
            IncludeTailFactor: true);

    public static Workspace Build(Func<DateTimeOffset>? clock = null)
    {
        var w = new Workspace(ClaimGenerator.Generate());
        var t0 = new DateTimeOffset(2025, 2, 10, 9, 0, 0, TimeSpan.Zero);
        var tick = 0;
        var svc = new ActuarialService(w, clock ?? (() => t0.AddHours(tick++ * 7)));

        w.User = "library.admin";
        Require(svc.PublishTables(LibraryV1()).Ok, "library");
        Require(svc.PublishTemplate(StandardTemplate()).Ok, "standard template");
        Require(svc.PublishTemplate(PaidOnlyTemplate()).Ok, "paid-only template");

        foreach (var (name, lob, region) in new[]
                 {
                     ("Motor", "Motor", "*"), ("Property North", "Property", "North"),
                     ("Property South", "Property", "South"), ("Liability", "Liability", "*")
                 })
        {
            var s = new Segment(Ids.Segment(name), name, new Dictionary<string, string> { ["lob"] = lob, ["region"] = region });
            w.Segments.Add(s);
        }

        // 2024: a complete, signed-off analysis.
        w.User = "a.martin";
        var v2024 = new DateOnly(2024, 12, 31);
        var motor = Ids.Segment("Motor");
        var a = svc.CreateAnalysis(motor, v2024, TriangleStructure.Fit(v2024, 12, 10, 12), Standard).Analysis;
        FillStandard(svc, a, tailPaid: 1.012m, tailInc: 1.004m);
        svc.SignOff(a, "c.reviewer");

        // 2025: rolled forward; the new accident year needs selections.
        var b = svc.RollForward(a, new DateOnly(2025, 12, 31), GrowthMode.Grow).Analysis;

        // Same date, another segment: a draft to review.
        svc.Duplicate(a, Ids.Segment("Property North"));

        // Later the library moves on. Pinned analyses do not change; an upgrade becomes available.
        w.User = "library.admin";
        Require(svc.PublishTables(new[] { PaidDevV2() }).Ok, "paid development v2");

        w.User = "a.martin";
        _ = b;
        return w;
    }

    /// <summary>Typical selections: 5-year volume-weighted factors, latest for the oldest ages, CL for mature years, BF for recent ones.</summary>
    public static void FillStandard(ActuarialService svc, Analysis a, decimal tailPaid, decimal tailInc)
    {
        var devs = a.Structure.DevelopmentKeys().ToList();
        var exs = a.Structure.ExposureKeys().ToList();

        foreach (var (d, i) in devs.Select((d, i) => (d, i)).Skip(1))
        {
            svc.Select(a, new StateKey(PaidDev, "selected", null, d), new SelectedStatistic(i >= devs.Count - 2 ? "latest" : "vwa_5"));
            svc.Select(a, new StateKey(IncurredDev, "selected", null, d), new SelectedStatistic("vwa_5"));
        }
        svc.Select(a, new StateKey(PaidDev, "tail", null, null), new ManualValue(tailPaid));
        svc.Select(a, new StateKey(IncurredDev, "tail", null, null), new ManualValue(tailInc));
        svc.Select(a, new StateKey(LossRatios, "selected", null, null), new SelectedStatistic("wa_all"));
        svc.Select(a, new StateKey(Selection, SelectionTableDefinition.WeightColumn(PaidCl), null, null), new ManualValue(2m));
        svc.Select(a, new StateKey(Selection, SelectionTableDefinition.WeightColumn(ExpectedClaims), null, null), new ManualValue(0m));

        foreach (var (e, i) in exs.Select((e, i) => (e, i)))
        {
            svc.Select(a, new StateKey(ExpectedClaims, "elr", e, null), new ManualValue(0.70m));
            var age = exs.Count - 1 - i;
            StateValue choice = age switch
            {
                >= 5 => new SelectedMethod(PaidCl),
                >= 3 => new SelectedMethod(IncurredCl),
                2 => new SelectedAverage(AverageKind.Weighted),
                _ => new SelectedMethod(Bf)
            };
            svc.Select(a, new StateKey(Selection, SelectionTableDefinition.SelectedUltimate, e, null), choice);
        }
    }

    private static void Require(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException($"Demo seed failed to publish the {what}.");
    }
}
