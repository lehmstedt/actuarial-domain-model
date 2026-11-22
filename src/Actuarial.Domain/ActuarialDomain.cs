// Actuarial domain model (C# 12 / .NET 8)
// Definition side = immutable + versioned, validated at publish.
// Instance side = pins versions, owns state.

using System.Text.Json.Serialization;
using Actuarial.Domain.Formulas;

namespace Actuarial.Domain;

// ───────────────────────── Shared value objects ─────────────────────────

public readonly record struct TableId(Guid Value)
{
    public static TableId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N")[..8];
}
public readonly record struct TableVersion(int Value)
{
    public override string ToString() => $"v{Value}";
}
public readonly record struct SegmentId(Guid Value);
public readonly record struct AnalysisId(Guid Value)
{
    public static AnalysisId New() => new(Guid.NewGuid());
}
public readonly record struct TemplateId(Guid Value);
public readonly record struct TemplateVersion(int Value)
{
    public override string ToString() => $"v{Value}";
}

/// <summary>Reference from one definition to another. Floating (null version) resolves to the latest published version.</summary>
public readonly record struct TableRef(TableId Table, TableVersion? Version)
{
    [JsonIgnore] public bool IsFloating => Version is null;
    public static TableRef Floating(TableId id) => new(id, null);
}

public readonly record struct PeriodLength(int Months);

/// <summary>A half-open interval in months, used to compare periods of different lengths.</summary>
public readonly record struct MonthSpan(int Start, int End)
{
    public bool Contains(MonthSpan other) => Start <= other.Start && other.End <= End;
    [JsonIgnore] public int Length => End - Start;
}

public static class Months
{
    /// <summary>Absolute month index (year * 12 + month - 1).</summary>
    public static int Index(DateOnly d) => d.Year * 12 + d.Month - 1;
    public static DateOnly FromIndex(int i) => new(i / 12, i % 12 + 1, 1);
    public static bool IsMonthEnd(DateOnly d) => d.AddDays(1).Day == 1;
    public static DateOnly MonthEnd(int index) => FromIndex(index + 1).AddDays(-1);
}

/// <summary>Identity of an exposure period: where it starts and how long it is.</summary>
public readonly record struct ExposureKey(DateOnly Start, int LengthMonths)
{
    [JsonIgnore] public MonthSpan Span { get { var m = Months.Index(Start); return new MonthSpan(m, m + LengthMonths); } }

    [JsonIgnore] public string Label => LengthMonths switch
    {
        12 when Start.Month == 1 => $"{Start.Year}",
        3 when (Start.Month - 1) % 3 == 0 => $"{Start.Year} Q{(Start.Month - 1) / 3 + 1}",
        6 when (Start.Month - 1) % 6 == 0 => $"{Start.Year} H{(Start.Month - 1) / 6 + 1}",
        1 => $"{Start:yyyy-MM}",
        _ => $"{Start:yyyy-MM} +{LengthMonths}m"
    };
}

/// <summary>Identity of a development period: AgeMonths is the START age of the interval (12-24 => 12).</summary>
public readonly record struct DevKey(int AgeMonths, int LengthMonths)
{
    [JsonIgnore] public MonthSpan Span => new(AgeMonths, AgeMonths + LengthMonths);

    /// <summary>Age at the end of the period, i.e. the age of the cumulative value in this triangle column.</summary>
    [JsonIgnore] public int EndAge => AgeMonths + LengthMonths;
    [JsonIgnore] public string Label => $"{AgeMonths}-{EndAge}";
}

public enum GrowthMode
{
    Grow,     // keep the first exposure period, add periods up to the new valuation date
    Slide     // keep the period counts, drop the oldest exposure period(s)
}

/// <summary>
/// Analysis-level layout. Definitions never depend on these counts.
/// Development period i is <c>DevKey(i * L, L)</c> and holds the cumulative value at age (i + 1) * L;
/// the age-to-age factor keyed by period i (i ≥ 1) develops age i * L to (i + 1) * L.
/// </summary>
public sealed record TriangleStructure(
    DateOnly FirstExposureStart,
    int ExposurePeriodCount,
    int DevelopmentPeriodCount,
    PeriodLength ExposurePeriodLength,
    PeriodLength DevelopmentPeriodLength)
{
    public static readonly int[] LegalLengths = { 1, 3, 6, 12 };

    public IEnumerable<ExposureKey> ExposureKeys() =>
        Enumerable.Range(0, ExposurePeriodCount)
            .Select(i => new ExposureKey(FirstExposureStart.AddMonths(i * ExposurePeriodLength.Months), ExposurePeriodLength.Months));

    public IEnumerable<DevKey> DevelopmentKeys() =>
        Enumerable.Range(0, DevelopmentPeriodCount)
            .Select(i => new DevKey(i * DevelopmentPeriodLength.Months, DevelopmentPeriodLength.Months));

    [JsonIgnore] public DateOnly LastExposureStart => FirstExposureStart.AddMonths((ExposurePeriodCount - 1) * ExposurePeriodLength.Months);

    public string Describe() =>
        $"{ExposurePeriodCount} × {ExposurePeriodLength.Months}m exposure from {FirstExposureStart:yyyy-MM}, " +
        $"{DevelopmentPeriodCount} × {DevelopmentPeriodLength.Months}m development";

    /// <summary>
    /// Mixing rule: a development period may be shorter than an exposure period (annual years with quarterly
    /// development) but must divide it, so every exposure period's diagonal lands on a development boundary.
    /// The structure must cover the valuation date with its last exposure period.
    /// </summary>
    public IEnumerable<string> Validate(DateOnly valuationDate)
    {
        if (FirstExposureStart.Day != 1) yield return "The first exposure period must start on the first day of a month.";
        if (ExposurePeriodCount < 1) yield return "There must be at least one exposure period.";
        if (DevelopmentPeriodCount < 1) yield return "There must be at least one development period.";

        var e = ExposurePeriodLength.Months;
        var d = DevelopmentPeriodLength.Months;
        if (!LegalLengths.Contains(e)) yield return $"Exposure period length must be one of {string.Join(", ", LegalLengths)} months.";
        if (!LegalLengths.Contains(d)) yield return $"Development period length must be one of {string.Join(", ", LegalLengths)} months.";
        if (d > 0 && e > 0 && (d > e || e % d != 0))
            yield return "The development period length must divide the exposure period length.";
        if (e > 0 && (FirstExposureStart.Month - 1) % e != 0)
            yield return $"A {e}-month exposure period must start on a {e}-month calendar boundary.";

        if (!Months.IsMonthEnd(valuationDate)) yield return "The valuation date must be a month end.";
        if (ExposurePeriodCount >= 1 && e > 0)
        {
            var v = Months.Index(valuationDate);
            var last = Months.Index(LastExposureStart);
            if (last > v) yield return "The last exposure period starts after the valuation date.";
            else if (last + e <= v) yield return "The last exposure period ends before the valuation date; the structure does not reach it.";
        }
    }

    /// <summary>
    /// Index of the latest development period known at the valuation date for an exposure period,
    /// or -1 if none is complete yet. Capped at the last development period.
    /// </summary>
    public int LatestDevIndex(ExposureKey exposure, DateOnly valuation)
    {
        var age = Months.Index(valuation) + 1 - Months.Index(exposure.Start);
        var idx = age / DevelopmentPeriodLength.Months - 1;
        return Math.Min(idx, DevelopmentPeriodCount - 1);
    }

    /// <summary>Structure for a later valuation date (valuation is assumed to fall at a month end).</summary>
    public TriangleStructure RollTo(DateOnly valuation, GrowthMode mode)
    {
        var months = Months.Index(valuation) - Months.Index(FirstExposureStart) + 1;
        var exposureCount = (int)Math.Ceiling(months / (double)ExposurePeriodLength.Months);
        var developmentCount = (int)Math.Ceiling(months / (double)DevelopmentPeriodLength.Months);

        return mode == GrowthMode.Grow
            ? this with { ExposurePeriodCount = exposureCount, DevelopmentPeriodCount = Math.Max(developmentCount, DevelopmentPeriodCount) }
            : this with { FirstExposureStart = FirstExposureStart.AddMonths((exposureCount - ExposurePeriodCount) * ExposurePeriodLength.Months) };
    }

    /// <summary>A structure whose last exposure period contains the valuation date, aligned to calendar boundaries.</summary>
    public static TriangleStructure Fit(DateOnly valuation, int exposureLength, int exposureCount, int developmentLength, int? developmentCount = null)
    {
        var v = Months.Index(valuation);
        var lastStart = v - (v % exposureLength);
        var first = Months.FromIndex(lastStart - (exposureCount - 1) * exposureLength);
        var dev = developmentCount ?? exposureCount * exposureLength / developmentLength;
        return new TriangleStructure(first, exposureCount, dev, new PeriodLength(exposureLength), new PeriodLength(developmentLength));
    }
}

// ───────────────────────── Schemas (the compatibility contract) ─────────────────────────

public enum MeasureKind { Paid, Incurred, ClaimCount, Premium, Factor, Ultimate, Other }
public enum BasisKind { Cumulative, Incremental, NotApplicable }

/// <summary>Shape of a column independent of triangle size.</summary>
public enum GrainKind { DevelopmentCell, DevelopmentPeriod, ExposurePeriod, Scalar }

public sealed record ColumnSchema(string Key, MeasureKind Measure, BasisKind Basis, GrainKind Grain);

public sealed record TableSchema(IReadOnlyList<ColumnSchema> Columns)
{
    public ColumnSchema? Find(string key) => Columns.FirstOrDefault(c => c.Key == key);
}

/// <summary>What a referrer needs from a column it consumes. Null Measure/Basis = don't care.</summary>
public sealed record InputRequirement(
    TableRef Source,
    string ColumnKey,
    GrainKind Grain,
    MeasureKind? Measure = null,
    BasisKind? Basis = null)
{
    /// <summary>Returns an error message, or null when the column satisfies the requirement.</summary>
    public string? Check(ColumnSchema c)
    {
        if (c.Grain != Grain) return $"expects grain {Grain} but '{c.Key}' is {c.Grain}";
        if (Measure is { } m && c.Measure != m) return $"expects measure {m} but '{c.Key}' is {c.Measure}";
        if (Basis is { } b && c.Basis != b) return $"expects basis {b} but '{c.Key}' is {c.Basis}";
        return null;
    }
}

// ───────────────────────── Definition side ─────────────────────────

/// <summary>Immutable once published. Editing = publishing a new version.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(InputTableDefinition), "input")]
[JsonDerivedType(typeof(ExhibitTableDefinition), "exhibit")]
[JsonDerivedType(typeof(DevelopmentExhibitDefinition), "development")]
[JsonDerivedType(typeof(MethodTableDefinition), "method")]
[JsonDerivedType(typeof(SelectionTableDefinition), "selection")]
public abstract record TableDefinition(TableId Id, TableVersion Version, string Name)
{
    /// <summary>Columns this table exposes to others.</summary>
    [JsonIgnore] public abstract TableSchema Output { get; }

    /// <summary>Everything this table consumes from other tables, with the shape it needs.</summary>
    [JsonIgnore] public abstract IReadOnlyCollection<InputRequirement> Requirements { get; }

    /// <summary>Edges of the dependency graph, derived from the requirements.</summary>
    [JsonIgnore] public IEnumerable<TableRef> Dependencies => Requirements.Select(r => r.Source).Distinct();

    [JsonIgnore] public abstract string KindLabel { get; }

    /// <summary>Internal consistency rules, checked at publish.</summary>
    public virtual IEnumerable<string> SelfCheck() => Enumerable.Empty<string>();

    /// <summary>Rules about what a reference points at (beyond schema satisfaction), checked at publish.</summary>
    public virtual IEnumerable<string> CheckTargets(Func<TableRef, TableDefinition?> resolve) => Enumerable.Empty<string>();

    /// <summary>Whether the user may enter an amount (ManualValue) in this column.</summary>
    public virtual bool AcceptsManualValue(string columnKey) => false;
}

// ----- Input table -----

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(DataSource), "data")]
[JsonDerivedType(typeof(FormulaSource), "formula")]
public abstract record InputSource;

/// <summary>ClaimField: paid, incurred, reported_count or premium.</summary>
public sealed record DataSource(string ClaimField, string Measure) : InputSource;
public sealed record FormulaSource(string Expression, IReadOnlyList<InputRequirement> Refs) : InputSource;

public sealed record InputTableDefinition(
    TableId Id, TableVersion Version, string Name, InputSource Source, TableSchema Schema)
    : TableDefinition(Id, Version, Name)
{
    public static readonly IReadOnlySet<string> ClaimFields = new HashSet<string> { "paid", "incurred", "reported_count", "premium" };

    [JsonIgnore] public override TableSchema Output => Schema;
    [JsonIgnore] public override string KindLabel => "Input";

    [JsonIgnore] public override IReadOnlyCollection<InputRequirement> Requirements =>
        Source is FormulaSource f ? f.Refs.ToArray() : Array.Empty<InputRequirement>();

    /// <summary>Cell overrides are allowed on every column of an input table.</summary>
    public override bool AcceptsManualValue(string columnKey) => Schema.Find(columnKey) is not null;

    public override IEnumerable<string> SelfCheck()
    {
        if (Schema.Columns.Count == 0) yield return "An input table must expose at least one column.";
        if (Schema.Columns.GroupBy(c => c.Key).Any(g => g.Count() > 1)) yield return "Duplicate column keys.";

        switch (Source)
        {
            case DataSource d:
                if (!ClaimFields.Contains(d.ClaimField))
                    yield return $"Unknown claim field '{d.ClaimField}'. Use one of: {string.Join(", ", ClaimFields)}.";
                foreach (var c in Schema.Columns)
                {
                    if (d.ClaimField == "premium" && c.Grain != GrainKind.ExposurePeriod)
                        yield return $"Premium has no development; column '{c.Key}' must be ExposurePeriod grain.";
                    if (c.Grain is GrainKind.DevelopmentPeriod or GrainKind.Scalar)
                        yield return $"A data column must be DevelopmentCell or ExposurePeriod grain ('{c.Key}' is {c.Grain}).";
                }
                break;

            case FormulaSource f:
                if (Schema.Columns.Count != 1) { yield return "A formula input table exposes exactly one column."; break; }
                if (FormulaProblem(f.Expression, f.Refs, Schema.Columns[0].Grain, _ => null) is { } p) yield return p;
                break;
        }
    }

    internal static string? FormulaProblem(string expression, IReadOnlyList<InputRequirement> refs, GrainKind target, Func<string, GrainKind?> column)
    {
        try
        {
            var g = Formula.InferGrain(Formula.Parse(expression), refs, column);
            return Formula.Fits(g, target) ? null : $"Formula '{expression}' yields {g} values, which do not fit a {target} column.";
        }
        catch (FormulaException e) { return $"Formula '{expression}': {e.Message}"; }
    }
}

// ----- Exhibit tables -----

public enum StatisticKind { SimpleAverage, VolumeWeightedAverage, Latest, Medial, Custom }

/// <summary>WindowSize = number of most recent exposure periods used; null = all.</summary>
public sealed record StatisticSpec(string Key, StatisticKind Kind, int? WindowSize)
{
    [JsonIgnore] public string Label => Kind switch
    {
        StatisticKind.SimpleAverage => "Simple avg",
        StatisticKind.VolumeWeightedAverage => "Vol-wtd avg",
        StatisticKind.Latest => "Latest",
        StatisticKind.Medial => "Medial avg",
        _ => "Custom"
    } + (WindowSize is { } w && Kind != StatisticKind.Latest ? $" {w}" : Kind is StatisticKind.Latest or StatisticKind.Custom ? "" : " all");
}

/// <summary>Common shape of exhibits: statistics the user picks from, stored as a "selected" column.</summary>
public abstract record ExhibitDefinition(TableId Id, TableVersion Version, string Name, IReadOnlyList<StatisticSpec> Statistics)
    : TableDefinition(Id, Version, Name)
{
    public const string SelectedColumn = "selected";

    public bool OffersStatistic(string key) => Statistics.Any(s => s.Key == key);

    /// <summary>A manual value may replace the selection, and a Custom statistic is entered by the user.</summary>
    public override bool AcceptsManualValue(string columnKey) =>
        columnKey == SelectedColumn || Statistics.Any(s => s.Key == columnKey && s.Kind == StatisticKind.Custom);

    protected IEnumerable<string> CheckStatistics(IEnumerable<string> reserved)
    {
        if (Statistics.Count == 0) yield return "An exhibit must offer at least one statistic.";
        if (Statistics.GroupBy(s => s.Key).Any(g => g.Count() > 1)) yield return "Duplicate statistic keys.";
        foreach (var s in Statistics)
        {
            if (reserved.Contains(s.Key)) yield return $"Statistic key '{s.Key}' is reserved.";
            if (s.WindowSize is <= 0) yield return $"Statistic '{s.Key}' has a non-positive window.";
        }
    }
}

/// <summary>
/// Generic exhibit. Inputs[0] is the analysed column; Inputs[1], when present, weights the volume-weighted average.
/// Statistics reduce the input: a development-cell input gives one value per development period,
/// an exposure-period input gives a scalar. The schema must declare those columns plus "selected".
/// </summary>
public sealed record ExhibitTableDefinition(
    TableId Id, TableVersion Version, string Name,
    IReadOnlyList<InputRequirement> Inputs,
    IReadOnlyList<StatisticSpec> Statistics,
    TableSchema Schema)
    : ExhibitDefinition(Id, Version, Name, Statistics)
{
    [JsonIgnore] public override TableSchema Output => Schema;
    [JsonIgnore] public override IReadOnlyCollection<InputRequirement> Requirements => Inputs.ToArray();
    [JsonIgnore] public override string KindLabel => "Exhibit";

    [JsonIgnore] public GrainKind? ReducedGrain => Inputs.Count == 0 ? null : Inputs[0].Grain switch
    {
        GrainKind.DevelopmentCell => GrainKind.DevelopmentPeriod,
        GrainKind.ExposurePeriod => GrainKind.Scalar,
        _ => null
    };

    public override IEnumerable<string> SelfCheck()
    {
        foreach (var p in CheckStatistics(new[] { SelectedColumn })) yield return p;
        if (Inputs.Count == 0) { yield return "An exhibit needs at least one input."; yield break; }
        if (ReducedGrain is not { } g) { yield return "The first input must be DevelopmentCell or ExposurePeriod grain."; yield break; }
        if (Inputs.Count > 1 && Inputs[1].Grain != Inputs[0].Grain) yield return "The weight input must have the same grain as the first input.";
        if (Inputs.Count > 2) yield return "An exhibit takes at most two inputs (values and weights).";

        foreach (var key in Statistics.Select(s => s.Key).Append(SelectedColumn))
        {
            var c = Schema.Find(key);
            if (c is null) yield return $"The schema must declare column '{key}'.";
            else if (c.Grain != g) yield return $"Column '{key}' must be {g} grain.";
        }
    }
}

/// <summary>Age-to-age factors from a cumulative input, with selectable statistics for a chain ladder.</summary>
public sealed record DevelopmentExhibitDefinition(
    TableId Id, TableVersion Version, string Name,
    TableRef Source, string SourceColumnKey,
    IReadOnlyList<StatisticSpec> Statistics,
    bool IncludeTailFactor)
    : ExhibitDefinition(Id, Version, Name, Statistics)
{
    public const string CdfColumn = "cdf";
    public const string TailColumn = "tail";

    [JsonIgnore] public override string KindLabel => "Development";

    [JsonIgnore] public override IReadOnlyCollection<InputRequirement> Requirements =>
        new[] { new InputRequirement(Source, SourceColumnKey, GrainKind.DevelopmentCell, null, BasisKind.Cumulative) };

    [JsonIgnore] public override TableSchema Output =>
        new(Statistics.Select(s => new ColumnSchema(s.Key, MeasureKind.Factor, BasisKind.NotApplicable, GrainKind.DevelopmentPeriod))
            .Append(new ColumnSchema(SelectedColumn, MeasureKind.Factor, BasisKind.NotApplicable, GrainKind.DevelopmentPeriod))
            .Append(new ColumnSchema(CdfColumn, MeasureKind.Factor, BasisKind.NotApplicable, GrainKind.DevelopmentPeriod))
            .Concat(IncludeTailFactor
                ? new[] { new ColumnSchema(TailColumn, MeasureKind.Factor, BasisKind.NotApplicable, GrainKind.Scalar) }
                : Array.Empty<ColumnSchema>())
            .ToList());

    public override bool AcceptsManualValue(string columnKey) =>
        base.AcceptsManualValue(columnKey) || (IncludeTailFactor && columnKey == TailColumn);

    public override IEnumerable<string> SelfCheck() => CheckStatistics(new[] { SelectedColumn, CdfColumn, TailColumn });
}

// ----- Method table -----

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(UserInputColumn), "input")]
[JsonDerivedType(typeof(FormulaColumn), "formula")]
public abstract record ColumnSource;
public sealed record UserInputColumn : ColumnSource;
public sealed record FormulaColumn(string Expression, IReadOnlyList<InputRequirement> Refs) : ColumnSource;

/// <summary>Method columns are always per exposure period.</summary>
public sealed record MethodColumn(
    string Key, string Label, ColumnSource Source,
    MeasureKind Measure, BasisKind Basis = BasisKind.NotApplicable);

public sealed record MethodTableDefinition(
    TableId Id, TableVersion Version, string Name,
    IReadOnlyList<MethodColumn> Columns,
    string UltimateColumnKey)
    : TableDefinition(Id, Version, Name)
{
    [JsonIgnore] public override string KindLabel => "Method";

    [JsonIgnore] public override TableSchema Output =>
        new(Columns.Select(c => new ColumnSchema(c.Key, c.Measure, c.Basis, GrainKind.ExposurePeriod)).ToList());

    [JsonIgnore] public override IReadOnlyCollection<InputRequirement> Requirements =>
        Columns.Select(c => c.Source).OfType<FormulaColumn>().SelectMany(f => f.Refs).ToArray();

    /// <summary>User-input columns take values; formula columns take overrides.</summary>
    public override bool AcceptsManualValue(string columnKey) => Columns.Any(c => c.Key == columnKey);

    /// <summary>Columns in an order where every formula comes after the columns it reads. Throws on a cycle.</summary>
    public IReadOnlyList<MethodColumn> EvaluationOrder()
    {
        var byKey = Columns.ToDictionary(c => c.Key);
        var order = new List<MethodColumn>();
        var state = new Dictionary<string, int>();

        void Visit(MethodColumn c)
        {
            if (state.TryGetValue(c.Key, out var s))
            {
                if (s == 1) throw new FormulaException($"Columns form a cycle through '{c.Key}'.");
                return;
            }
            state[c.Key] = 1;
            if (c.Source is FormulaColumn f)
                foreach (var dep in Formula.ColumnReferences(Formula.Parse(f.Expression)).Distinct())
                    if (byKey.TryGetValue(dep, out var d)) Visit(d);
            state[c.Key] = 2;
            order.Add(c);
        }

        foreach (var c in Columns) Visit(c);
        return order;
    }

    public override IEnumerable<string> SelfCheck()
    {
        if (Columns.Count == 0) yield return "A method table needs at least one column.";
        if (Columns.GroupBy(c => c.Key).Any(g => g.Count() > 1))
            yield return "Duplicate column keys.";

        var ult = Columns.FirstOrDefault(c => c.Key == UltimateColumnKey);
        if (ult is null) yield return $"Ultimate column '{UltimateColumnKey}' does not exist.";
        else if (ult.Measure != MeasureKind.Ultimate) yield return $"Ultimate column '{UltimateColumnKey}' must have measure Ultimate.";

        var keys = Columns.Select(c => c.Key).ToHashSet();
        GrainKind? Column(string k) => keys.Contains(k) ? GrainKind.ExposurePeriod : null;

        var formulasOk = true;
        foreach (var c in Columns)
            if (c.Source is FormulaColumn f &&
                InputTableDefinition.FormulaProblem(f.Expression, f.Refs, GrainKind.ExposurePeriod, Column) is { } p)
            {
                formulasOk = false;
                yield return $"Column '{c.Key}': {p}";
            }

        if (formulasOk)
        {
            string? cycle = null;
            try { EvaluationOrder(); } catch (FormulaException e) { cycle = e.Message; }
            if (cycle is not null) yield return cycle;
        }
    }
}

// ----- Selection table -----

public sealed record MethodRef(TableRef Table, string UltimateColumnKey);

public enum AverageKind { Arithmetic, Weighted }

/// <summary>
/// Per exposure period the user picks an average OR a method's ultimate (or types an amount).
/// Averages run over the selectable methods' ultimates; the weighted average uses one scalar weight per
/// method (default 1), keyed by the method table so weights survive a reordering. Inputs are shown alongside.
/// The choice itself lives in the analysis state.
/// </summary>
public sealed record SelectionTableDefinition(
    TableId Id, TableVersion Version, string Name,
    IReadOnlyList<InputRequirement> Inputs,
    IReadOnlyList<AverageKind> Averages,
    IReadOnlyList<MethodRef> SelectableMethods)
    : TableDefinition(Id, Version, Name)
{
    public const string SelectedUltimate = "selected_ultimate";
    public static string AverageColumn(AverageKind k) => k == AverageKind.Arithmetic ? "arithmetic_average" : "weighted_average";
    public static string WeightColumn(TableId method) => $"weight:{method.Value:N}";

    [JsonIgnore] public override string KindLabel => "Selection";

    [JsonIgnore] public override TableSchema Output =>
        new(new[] { new ColumnSchema(SelectedUltimate, MeasureKind.Ultimate, BasisKind.NotApplicable, GrainKind.ExposurePeriod) }
            .Concat(Averages.Distinct().Select(a => new ColumnSchema(AverageColumn(a), MeasureKind.Ultimate, BasisKind.NotApplicable, GrainKind.ExposurePeriod)))
            .Concat(Averages.Contains(AverageKind.Weighted)
                ? SelectableMethods.Select(m => new ColumnSchema(WeightColumn(m.Table.Table), MeasureKind.Other, BasisKind.NotApplicable, GrainKind.Scalar))
                : Enumerable.Empty<ColumnSchema>())
            .ToList());

    [JsonIgnore] public override IReadOnlyCollection<InputRequirement> Requirements =>
        Inputs.Concat(SelectableMethods.Select(m =>
                new InputRequirement(m.Table, m.UltimateColumnKey, GrainKind.ExposurePeriod, MeasureKind.Ultimate)))
              .ToArray();

    public bool OffersMethod(TableId id) => SelectableMethods.Any(m => m.Table.Table == id);

    public override bool AcceptsManualValue(string columnKey) =>
        columnKey == SelectedUltimate || columnKey.StartsWith("weight:", StringComparison.Ordinal) && Output.Find(columnKey) is not null;

    public override IEnumerable<string> SelfCheck()
    {
        if (SelectableMethods.Count == 0) yield return "A selection table needs at least one selectable method.";
        if (SelectableMethods.GroupBy(m => m.Table.Table).Any(g => g.Count() > 1)) yield return "A method is listed twice.";
        if (Averages.Distinct().Count() != Averages.Count) yield return "An average is listed twice.";
        foreach (var i in Inputs)
            if (i.Grain != GrainKind.ExposurePeriod) yield return $"Input '{i.ColumnKey}' must be ExposurePeriod grain.";
    }

    public override IEnumerable<string> CheckTargets(Func<TableRef, TableDefinition?> resolve)
    {
        foreach (var m in SelectableMethods)
            switch (resolve(m.Table))
            {
                case MethodTableDefinition mt when mt.UltimateColumnKey != m.UltimateColumnKey:
                    yield return $"'{mt.Name}' designates '{mt.UltimateColumnKey}' as its ultimate, not '{m.UltimateColumnKey}'.";
                    break;
                case MethodTableDefinition: break;
                case { } other: yield return $"'{other.Name}' is not a method table and cannot be selected."; break;
            }
    }
}

// ----- Library (publish is the only gate) -----

public sealed record PublishError(TableId Table, TableVersion Version, string Message);

public sealed record PublishResult(IReadOnlyList<PublishError> Errors)
{
    public bool Ok => Errors.Count == 0;
}

public sealed class Library
{
    private Dictionary<(TableId, TableVersion), TableDefinition> _defs = new();

    public TableDefinition Get(TableId id, TableVersion v) => _defs[(id, v)];
    public TableDefinition? Find(TableId id, TableVersion v) => _defs.GetValueOrDefault((id, v));
    public TableDefinition? FindLatest(TableId id) => Latest(id) is { } v ? _defs[(id, v)] : null;

    public TableVersion? Latest(TableId id) => LatestIn(_defs, id);

    /// <summary>The library allocates versions: the next one is always latest + 1.</summary>
    public TableVersion NextVersion(TableId id) => new((Latest(id)?.Value ?? 0) + 1);

    public IEnumerable<TableDefinition> All => _defs.Values;
    public IEnumerable<TableId> TableIds => _defs.Keys.Select(k => k.Item1).Distinct();
    public IEnumerable<TableDefinition> Versions(TableId id) => _defs.Values.Where(d => d.Id == id).OrderBy(d => d.Version.Value);

    /// <summary>Tables whose latest version references the given table (floating or pinned to any version).</summary>
    public IEnumerable<TableDefinition> Dependents(TableId id) =>
        TableIds.Select(t => FindLatest(t)!).Where(d => d.Dependencies.Any(r => r.Table == id));

    /// <summary>Loads definitions that were validated when they were first published (persistence). No checks.</summary>
    public static Library Restore(IEnumerable<TableDefinition> definitions)
    {
        var lib = new Library();
        foreach (var d in definitions) lib._defs[(d.Id, d.Version)] = d;
        return lib;
    }

    private static TableVersion? LatestIn(Dictionary<(TableId, TableVersion), TableDefinition> defs, TableId id)
    {
        var versions = defs.Keys.Where(k => k.Item1 == id).Select(k => k.Item2.Value).ToList();
        return versions.Count == 0 ? null : new TableVersion(versions.Max());
    }

    /// <summary>
    /// Atomic: the batch is staged, everything affected is validated, and nothing is committed unless all checks pass.
    /// Mutually dependent updates (a changed input + its dependent exhibits) therefore validate together.
    /// </summary>
    public PublishResult Publish(IReadOnlyCollection<TableDefinition> batch)
    {
        var errors = new List<PublishError>();
        var staged = new Dictionary<(TableId, TableVersion), TableDefinition>(_defs);

        if (batch.Count == 0) return new(new[] { new PublishError(default, default, "Nothing to publish.") });

        foreach (var g in batch.GroupBy(d => d.Id).Where(g => g.Count() > 1))
            errors.Add(new(g.Key, g.First().Version, "A batch may publish only one version of each table."));

        foreach (var d in batch)
        {
            if (d.Version.Value < 1)
                errors.Add(new(d.Id, d.Version, "Versions start at 1."));
            else if (_defs.ContainsKey((d.Id, d.Version)))
                errors.Add(new(d.Id, d.Version, "Versions are immutable; this version already exists."));
            else if (LatestIn(_defs, d.Id) is { } latest && d.Version.Value != latest.Value + 1)
                errors.Add(new(d.Id, d.Version, $"The next version of this table is v{latest.Value + 1}."));
            else if (LatestIn(_defs, d.Id) is null && d.Version.Value != 1)
                errors.Add(new(d.Id, d.Version, "A new table starts at v1."));
            else if (string.IsNullOrWhiteSpace(d.Name))
                errors.Add(new(d.Id, d.Version, "A table needs a name."));
            staged[(d.Id, d.Version)] = d;
        }
        if (errors.Count > 0) return new(errors);

        // Validate: everything in the batch, plus the latest version of every table
        // (their floating refs now resolve against the new latest versions).
        var toValidate = new Dictionary<(TableId, TableVersion), TableDefinition>();
        foreach (var d in batch) toValidate[(d.Id, d.Version)] = d;
        foreach (var id in staged.Keys.Select(k => k.Item1).Distinct())
        {
            var v = LatestIn(staged, id)!.Value;
            toValidate[(id, v)] = staged[(id, v)];
        }

        TableDefinition? Resolve(TableRef r)
        {
            var ver = r.Version ?? LatestIn(staged, r.Table);
            return ver is null ? null : staged.GetValueOrDefault((r.Table, ver.Value));
        }

        foreach (var def in toValidate.Values)
        {
            foreach (var msg in def.SelfCheck())
                errors.Add(new(def.Id, def.Version, msg));

            foreach (var req in def.Requirements)
            {
                if (Resolve(req.Source) is not { } target)
                {
                    errors.Add(new(def.Id, def.Version, $"Unresolved reference to table {req.Source.Table}{(req.Source.Version is { } rv ? " " + rv : "")}."));
                    continue;
                }

                var col = target.Output.Find(req.ColumnKey);
                var hint = req.Source.IsFloating
                    ? " (floating ref: breaking change in the referenced table — re-publish this dependent in the same batch)"
                    : "";
                if (col is null)
                {
                    errors.Add(new(def.Id, def.Version,
                        $"Table '{target.Name}' v{target.Version.Value} has no column '{req.ColumnKey}'.{hint}"));
                    continue;
                }

                if (req.Check(col) is { } problem)
                    errors.Add(new(def.Id, def.Version, $"'{target.Name}' v{target.Version.Value}: {problem}{hint}"));
            }

            foreach (var msg in def.CheckTargets(Resolve))
                errors.Add(new(def.Id, def.Version, msg));
        }

        DetectCycles(staged, errors);
        if (errors.Count > 0) return new(errors.Distinct().ToList());

        _defs = staged;       // commit
        return new(errors);
    }

    private static void DetectCycles(
        Dictionary<(TableId, TableVersion), TableDefinition> defs, List<PublishError> errors)
    {
        var state = new Dictionary<(TableId, TableVersion), int>();   // 1 = visiting, 2 = done

        void Visit((TableId, TableVersion) key)
        {
            if (state.TryGetValue(key, out var s))
            {
                if (s == 1) errors.Add(new(key.Item1, key.Item2, "Dependency cycle detected."));
                return;
            }
            state[key] = 1;
            foreach (var dep in defs[key].Dependencies)
            {
                var v = dep.Version ?? LatestIn(defs, dep.Table);
                if (v is not null && defs.ContainsKey((dep.Table, v.Value)))
                    Visit((dep.Table, v.Value));
            }
            state[key] = 2;
        }

        foreach (var key in defs.Keys) Visit(key);
    }
}

// ----- Analysis template (presentation tree) -----

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(ObjectNode), "object")]
[JsonDerivedType(typeof(CollectionNode), "collection")]
[JsonDerivedType(typeof(FolderNode), "folder")]
public abstract record TemplateNode(Guid NodeId, string Name);

public sealed record ObjectNode(Guid NodeId, string Name, TableRef Table) : TemplateNode(NodeId, Name);

public sealed record CollectionNode(Guid NodeId, string Name, IReadOnlyList<ObjectNode> Objects)
    : TemplateNode(NodeId, Name);                    // collections contain objects only

public sealed record FolderNode(Guid NodeId, string Name, IReadOnlyList<TemplateNode> Children)
    : TemplateNode(NodeId, Name)                     // folder: folders or collections only
{
    public static FolderNode Create(Guid id, string name, IEnumerable<TemplateNode> children)
    {
        var list = children.ToList();
        if (list.Any(c => c is ObjectNode))
            throw new ArgumentException("A folder cannot directly contain objects.");
        return new FolderNode(id, name, list);
    }
}

public sealed record AnalysisTemplate(
    TemplateId Id, TemplateVersion Version, string Name, FolderNode Root)
{
    public IEnumerable<ObjectNode> Objects() => Walk(Root);

    private static IEnumerable<ObjectNode> Walk(TemplateNode n) => n switch
    {
        ObjectNode o     => new[] { o },
        CollectionNode c => c.Objects,
        FolderNode f     => f.Children.SelectMany(Walk),
        _                => Enumerable.Empty<ObjectNode>()
    };

    /// <summary>Structural rules (the factory rule, re-checked because positional construction bypasses it).</summary>
    public IEnumerable<string> SelfCheck()
    {
        if (string.IsNullOrWhiteSpace(Name)) yield return "A template needs a name.";
        if (!Objects().Any()) yield return "A template must show at least one object.";

        var ids = new HashSet<Guid>();
        IEnumerable<string> Check(TemplateNode n)
        {
            if (!ids.Add(n.NodeId)) yield return $"Node id {n.NodeId} is used twice.";
            if (n is FolderNode f)
                foreach (var c in f.Children)
                {
                    if (c is ObjectNode) yield return $"Folder '{f.Name}' cannot directly contain object '{c.Name}'.";
                    foreach (var p in Check(c)) yield return p;
                }
            if (n is CollectionNode col)
                foreach (var o in col.Objects)
                    foreach (var p in Check(o)) yield return p;
        }
        foreach (var p in Check(Root)) yield return p;
    }
}

public sealed record TemplatePublishResult(IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>Templates are versioned like tables: publishing validates the tree and that its closure resolves.</summary>
public sealed class TemplateLibrary
{
    private readonly Dictionary<(TemplateId, TemplateVersion), AnalysisTemplate> _templates = new();

    public AnalysisTemplate Get(TemplateId id, TemplateVersion v) => _templates[(id, v)];
    public AnalysisTemplate? FindLatest(TemplateId id) => Latest(id) is { } v ? _templates[(id, v)] : null;
    public TemplateVersion? Latest(TemplateId id)
    {
        var vs = _templates.Keys.Where(k => k.Item1 == id).Select(k => k.Item2.Value).ToList();
        return vs.Count == 0 ? null : new TemplateVersion(vs.Max());
    }
    public TemplateVersion NextVersion(TemplateId id) => new((Latest(id)?.Value ?? 0) + 1);
    public IEnumerable<AnalysisTemplate> All => _templates.Values;
    public IEnumerable<TemplateId> TemplateIds => _templates.Keys.Select(k => k.Item1).Distinct();
    public IEnumerable<AnalysisTemplate> Versions(TemplateId id) => _templates.Values.Where(t => t.Id == id).OrderBy(t => t.Version.Value);

    public static TemplateLibrary Restore(IEnumerable<AnalysisTemplate> templates)
    {
        var lib = new TemplateLibrary();
        foreach (var t in templates) lib._templates[(t.Id, t.Version)] = t;
        return lib;
    }

    public TemplatePublishResult Publish(AnalysisTemplate template, Library library)
    {
        var errors = template.SelfCheck().ToList();

        var expected = NextVersion(template.Id);
        if (template.Version != expected)
            errors.Add($"The next version of this template is v{expected.Value}.");

        foreach (var o in template.Objects())
        {
            var v = o.Table.Version ?? library.Latest(o.Table.Table);
            if (v is null || library.Find(o.Table.Table, v.Value) is null)
                errors.Add($"Object '{o.Name}' refers to a table that is not in the library.");
        }

        if (errors.Count == 0)
        {
            try { ManifestBuilder.Build(template, library); }
            catch (InvalidOperationException e) { errors.Add(e.Message); }
        }

        if (errors.Count == 0) _templates[(template.Id, template.Version)] = template;
        return new TemplatePublishResult(errors);
    }
}

// ───────────────────────── Instance side ─────────────────────────

/// <summary>Filters select raw claim rows by attribute; "*" or a missing attribute means any value.</summary>
public sealed record Segment(SegmentId Id, string Name, IReadOnlyDictionary<string, string> Filters);

/// <summary>Frozen copy (or content hash + pointer) of the claim data as of the valuation date.</summary>
public sealed record DataSnapshot(Guid Id, SegmentId Segment, DateOnly ValuationDate, string ContentHash);

/// <summary>Every (table, version) reachable from the template, floating refs resolved. Frozen at creation.</summary>
public sealed record ResolvedManifest(IReadOnlyDictionary<TableId, TableVersion> Pinned)
{
    public TableVersion Resolve(TableRef r) => r.Version ?? Pinned[r.Table];

    /// <summary>Pinned tables in dependency order (each table after every table it reads).</summary>
    public IReadOnlyList<TableId> TopologicalOrder(Library library)
    {
        var order = new List<TableId>();
        var seen = new HashSet<TableId>();
        void Visit(TableId id)
        {
            if (!seen.Add(id)) return;
            foreach (var dep in library.Get(id, Pinned[id]).Dependencies) Visit(dep.Table);
            order.Add(id);
        }
        foreach (var id in Pinned.Keys.OrderBy(k => k.Value)) Visit(id);
        return order;
    }
}

/// <summary>
/// Compatibility was already enforced at publish, so this only resolves versions and checks completeness.
/// An analysis holds exactly one version per table.
/// </summary>
public static class ManifestBuilder
{
    /// <param name="preferred">Existing pins win over "latest" for floating refs, so changing a template never silently upgrades a table.</param>
    public static ResolvedManifest Build(AnalysisTemplate template, Library library, ResolvedManifest? preferred = null)
    {
        var pinned = new Dictionary<TableId, TableVersion>();

        void Visit(TableRef r)
        {
            var v = r.Version ?? (preferred is not null && preferred.Pinned.TryGetValue(r.Table, out var p) ? p : library.Latest(r.Table))
                ?? throw new InvalidOperationException($"Table {r.Table} is not in the library.");

            if (pinned.TryGetValue(r.Table, out var existing))
            {
                if (existing != v)
                    throw new InvalidOperationException(
                        $"Table '{library.Get(r.Table, existing).Name}' is required at both v{existing.Value} and v{v.Value}; an analysis holds one version per table.");
                return;
            }

            pinned[r.Table] = v;
            foreach (var dep in library.Get(r.Table, v).Dependencies) Visit(dep);
        }

        foreach (var o in template.Objects()) Visit(o.Table);
        return new ResolvedManifest(pinned);
    }
}

/// <summary>Key of a user-entered value or selection, by what it means (period identity), not by position.</summary>
public readonly record struct StateKey(TableId Table, string ColumnKey, ExposureKey? Exposure, DevKey? Development);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(SelectedStatistic), "statistic")]
[JsonDerivedType(typeof(SelectedMethod), "method")]
[JsonDerivedType(typeof(SelectedAverage), "average")]
[JsonDerivedType(typeof(ManualValue), "value")]
public abstract record StateValue;
public sealed record SelectedStatistic(string StatisticKey) : StateValue;      // exhibit: which statistic per age (or overall)
public sealed record SelectedMethod(TableId MethodTable) : StateValue;         // selection table: method per exposure period
public sealed record SelectedAverage(AverageKind Average) : StateValue;        // selection table: average per exposure period
public sealed record ManualValue(decimal Value) : StateValue;                  // amounts: inputs, overrides, factors, weights

public enum ProvenanceKind { Entered, CarriedForward, Remapped }

public sealed record Provenance(ProvenanceKind Kind, AnalysisId? From = null, int? FromRevision = null);

public sealed record StateEntry(StateValue Value, Provenance Provenance);

/// <summary>State that no longer maps onto the analysis. Kept (not deleted) so it can come back.</summary>
public sealed record DormantEntry(StateEntry Entry, string Reason);

/// <summary>Is a piece of state meaningful for the pinned definitions? Shared by user entry and migration.</summary>
public static class StateValidator
{
    /// <summary>Returns why the entry cannot live in the analysis, or null when it is valid.</summary>
    public static string? Problem(StateKey k, StateValue value, ResolvedManifest manifest, Library library)
        => SchemaProblem(k, manifest, library) ?? ValueProblem(k, value, library.Get(k.Table, manifest.Pinned[k.Table]));

    /// <summary>The column exists in the pinned version and the key has the right shape for its grain.</summary>
    public static string? SchemaProblem(StateKey k, ResolvedManifest manifest, Library library)
    {
        if (!manifest.Pinned.TryGetValue(k.Table, out var version))
            return "table is not part of this analysis";

        var col = library.Get(k.Table, version).Output.Find(k.ColumnKey);
        if (col is null)
            return $"column '{k.ColumnKey}' does not exist in v{version.Value}";

        var shapeOk = col.Grain switch
        {
            GrainKind.Scalar => k.Exposure is null && k.Development is null,
            GrainKind.ExposurePeriod => k.Exposure is not null && k.Development is null,
            GrainKind.DevelopmentPeriod => k.Exposure is null && k.Development is not null,
            GrainKind.DevelopmentCell => k.Exposure is not null && k.Development is not null,
            _ => false
        };
        return shapeOk ? null : $"column grain is now {col.Grain}";
    }

    /// <summary>The value makes sense for this definition: the statistic or method is still offered, the column takes amounts.</summary>
    public static string? ValueProblem(StateKey k, StateValue value, TableDefinition def) => (value, def) switch
    {
        (SelectedStatistic s, ExhibitDefinition ex) when k.ColumnKey == ExhibitDefinition.SelectedColumn =>
            ex.OffersStatistic(s.StatisticKey) ? null : $"statistic '{s.StatisticKey}' is not offered by v{def.Version.Value}",
        (SelectedMethod m, SelectionTableDefinition sel) when k.ColumnKey == SelectionTableDefinition.SelectedUltimate =>
            sel.OffersMethod(m.MethodTable) ? null : $"the method is not selectable in v{def.Version.Value}",
        (SelectedAverage a, SelectionTableDefinition sel) when k.ColumnKey == SelectionTableDefinition.SelectedUltimate =>
            sel.Averages.Contains(a.Average) ? null : $"the {a.Average.ToString().ToLowerInvariant()} average is not offered by v{def.Version.Value}",
        (ManualValue, _) =>
            def.AcceptsManualValue(k.ColumnKey) ? null : $"column '{k.ColumnKey}' does not take entered values in v{def.Version.Value}",
        _ => $"a {value.GetType().Name} cannot be stored in column '{k.ColumnKey}' of a {def.KindLabel.ToLowerInvariant()} table"
    };

    /// <summary>Selections and inputs the analysis expects but does not hold yet.</summary>
    public static IReadOnlyList<StateKey> Missing(
        IReadOnlyDictionary<StateKey, StateEntry> state, TriangleStructure structure, ResolvedManifest manifest, Library library)
    {
        var toEx = structure.ExposureKeys().ToList();
        var toDev = structure.DevelopmentKeys().ToList();
        var needs = new List<StateKey>();
        void Need(StateKey k) { if (!state.ContainsKey(k)) needs.Add(k); }

        foreach (var (id, ver) in manifest.Pinned)
        {
            switch (library.Get(id, ver))
            {
                case SelectionTableDefinition:
                    foreach (var e in toEx) Need(new StateKey(id, SelectionTableDefinition.SelectedUltimate, e, null));
                    break;

                case DevelopmentExhibitDefinition:
                    // Period 0 has no age-to-age factor (nothing develops into the first age).
                    foreach (var d in toDev.Skip(1)) Need(new StateKey(id, ExhibitDefinition.SelectedColumn, null, d));
                    break;

                case ExhibitTableDefinition ex:
                    if (ex.ReducedGrain == GrainKind.Scalar) Need(new StateKey(id, ExhibitDefinition.SelectedColumn, null, null));
                    else foreach (var d in toDev) Need(new StateKey(id, ExhibitDefinition.SelectedColumn, null, d));
                    break;

                case MethodTableDefinition m:
                    foreach (var c in m.Columns.Where(c => c.Source is UserInputColumn))
                        foreach (var e in toEx) Need(new StateKey(id, c.Key, e, null));
                    break;
            }
        }
        return needs;
    }
}

public enum AnalysisStatus { Draft, SignedOff }

public sealed class Analysis
{
    public AnalysisId Id { get; }
    public int Revision { get; }
    public SegmentId Segment { get; }
    public DateOnly ValuationDate { get; }
    public TriangleStructure Structure { get; private set; }
    public TemplateId Template { get; private set; }
    public TemplateVersion TemplateVersion { get; private set; }
    public ResolvedManifest Manifest { get; private set; }
    public Lineage? DerivedFrom { get; init; }
    public DataSnapshot Data { get; }
    public string EngineVersion { get; private set; }

    private readonly Dictionary<StateKey, StateEntry> _state = new();
    private readonly Dictionary<StateKey, DormantEntry> _dormant = new();
    public IReadOnlyDictionary<StateKey, StateEntry> State => _state;
    public IReadOnlyDictionary<StateKey, DormantEntry> Dormant => _dormant;

    public AnalysisStatus Status { get; private set; } = AnalysisStatus.Draft;
    public SignOff? SignOff { get; private set; }

    public Analysis(AnalysisId id, int revision, SegmentId segment, DateOnly valuationDate,
        TriangleStructure structure, TemplateId template, TemplateVersion templateVersion,
        ResolvedManifest manifest, DataSnapshot data, string engineVersion)
    {
        if (data.Segment != segment || data.ValuationDate != valuationDate)
            throw new ArgumentException("The data snapshot must belong to the analysis' segment and valuation date.");
        (Id, Revision, Segment, ValuationDate, Structure, Template, TemplateVersion, Manifest, Data, EngineVersion)
            = (id, revision, segment, valuationDate, structure, template, templateVersion, manifest, data, engineVersion);
    }

    /// <summary>Rehydrates a stored analysis exactly as it was (persistence).</summary>
    public static Analysis Restore(AnalysisId id, int revision, SegmentId segment, DateOnly valuationDate,
        TriangleStructure structure, TemplateId template, TemplateVersion templateVersion,
        ResolvedManifest manifest, DataSnapshot data, string engineVersion, Lineage? derivedFrom,
        IEnumerable<KeyValuePair<StateKey, StateEntry>> state, IEnumerable<KeyValuePair<StateKey, DormantEntry>> dormant,
        SignOff? signOff)
    {
        var a = new Analysis(id, revision, segment, valuationDate, structure, template, templateVersion, manifest, data, engineVersion)
        {
            DerivedFrom = derivedFrom
        };
        a.LoadState(state, dormant);
        if (signOff is not null) { a.Status = AnalysisStatus.SignedOff; a.SignOff = signOff; }
        return a;
    }

    public void Select(StateKey key, StateValue value)
    {
        RequireDraft();
        _state[key] = new StateEntry(value, new Provenance(ProvenanceKind.Entered));
        _dormant.Remove(key);
    }

    /// <summary>Removes a user entry from a draft. (Migration never deletes; this is the user's own edit.)</summary>
    public bool Clear(StateKey key)
    {
        RequireDraft();
        return _state.Remove(key);
    }

    /// <summary>Replaces all state. Used by operations after migration.</summary>
    internal void LoadState(
        IEnumerable<KeyValuePair<StateKey, StateEntry>> active,
        IEnumerable<KeyValuePair<StateKey, DormantEntry>> dormant)
    {
        var a = active.ToList();
        var d = dormant.ToList();
        _state.Clear();
        _dormant.Clear();
        foreach (var (k, v) in a) _state[k] = v;
        foreach (var (k, v) in d) _dormant[k] = v;
    }

    /// <summary>In-place reconfiguration (apply template, reshape). Drafts only.</summary>
    internal void Reconfigure(TriangleStructure structure, TemplateId template, TemplateVersion version, ResolvedManifest manifest)
    {
        RequireDraft();
        (Structure, Template, TemplateVersion, Manifest) = (structure, template, version, manifest);
    }

    public void Sign(string user, ResultSnapshot results, DateTimeOffset at)
    {
        RequireDraft();
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Sign-off needs a user.");
        if (string.IsNullOrWhiteSpace(results.Hash)) throw new ArgumentException("Results must carry their hash.");
        if (results.EngineVersion != EngineVersion)
            throw new InvalidOperationException($"Results were computed by engine {results.EngineVersion}, but the analysis pins {EngineVersion}.");
        Status = AnalysisStatus.SignedOff;
        SignOff = new SignOff(user, at, results);
    }

    /// <summary>
    /// Upgrading never mutates: it yields a new Draft revision of the same analysis for the user to review.
    /// State is carried as is; AnalysisOperations.NewRevision migrates it when the manifest changes.
    /// </summary>
    public Analysis NewRevision(TemplateVersion tv, ResolvedManifest manifest, string engine)
    {
        var next = new Analysis(Id, Revision + 1, Segment, ValuationDate, Structure,
            Template, tv, manifest, Data, engine)
        {
            DerivedFrom = new Lineage(Operation.NewRevision, Id, Revision)
        };
        next.LoadState(_state, _dormant);
        return next;
    }

    private void RequireDraft()
    {
        if (Status == AnalysisStatus.SignedOff)
            throw new InvalidOperationException("Signed-off analyses are immutable. Create a new revision first.");
    }
}

public sealed record SignOff(string User, DateTimeOffset At, ResultSnapshot Results);

/// <summary>
/// Materialised values at sign-off. Hash covers the inputs that determine results — manifest, structure,
/// valuation date, data hash, active state and engine version — and the results themselves.
/// Dormant state is stored with the analysis but is not hashed: it cannot affect any number.
/// </summary>
public sealed record ResultSnapshot(
    string Hash,
    string EngineVersion,
    IReadOnlyDictionary<TableId, TableResult> Tables);

/// <summary>Values by column. Shape follows the grain: [exposure][development], [1][development], [exposure][1] or [1][1].</summary>
public sealed record ColumnResult(string Key, GrainKind Grain, decimal?[][] Values, bool IsOutput = true)
{
    public decimal? At(int exposure, int development) => Grain switch
    {
        GrainKind.DevelopmentCell => Values[exposure][development],
        GrainKind.DevelopmentPeriod => Values[0][development],
        GrainKind.ExposurePeriod => Values[exposure][0],
        _ => Values[0][0]
    };
}

public sealed record TableResult(IReadOnlyList<ColumnResult> Columns, IReadOnlyList<string> Problems)
{
    public ColumnResult? Column(string key) => Columns.FirstOrDefault(c => c.Key == key);
}

// ───────────────────────── Computation ─────────────────────────

public interface ICalculationEngine
{
    string Version { get; }

    /// <summary>
    /// Pure function: same manifest + structure + data + state => same results. The template is presentation
    /// only and is deliberately not an input. Drafts compute on read; sign-off persists.
    /// </summary>
    ResultSnapshot Compute(Analysis analysis, Library library);
}
