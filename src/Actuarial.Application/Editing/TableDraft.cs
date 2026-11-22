using System.Text;
using Actuarial.Domain;
using Actuarial.Domain.Formulas;

namespace Actuarial.Application.Editing;

// Editable forms behind the library editor. Definitions stay immutable records: a draft is a mutable working copy
// that the UI binds to, and Build() turns it into the next version (or v1) of a definition. Publishing still goes
// through ActuarialService → Library.Publish, which is the only gate.

public enum TableKind { Input, Development, Exhibit, Method, Selection }

public static class TableKinds
{
    public static TableKind Of(TableDefinition d) => d switch
    {
        InputTableDefinition => TableKind.Input,
        DevelopmentExhibitDefinition => TableKind.Development,
        ExhibitTableDefinition => TableKind.Exhibit,
        MethodTableDefinition => TableKind.Method,
        SelectionTableDefinition => TableKind.Selection,
        _ => throw new ArgumentOutOfRangeException(nameof(d))
    };

    public static string Label(TableKind k) => k switch
    {
        TableKind.Input => "Input table",
        TableKind.Development => "Development exhibit",
        TableKind.Exhibit => "Exhibit",
        TableKind.Method => "Method table",
        _ => "Selection table"
    };

    public static string Describe(TableKind k) => k switch
    {
        TableKind.Input => "Claim data or a triangle computed by a formula from other tables.",
        TableKind.Development => "Age-to-age factors from a cumulative triangle, with statistics to select and a chain ladder.",
        TableKind.Exhibit => "Statistics over a column (e.g. loss ratios), with a selected value.",
        TableKind.Method => "Columns per exposure period from formulas or user input, with a designated ultimate.",
        _ => "Picks a method's ultimate or an average of them, per exposure period."
    };
}

public static class Keys
{
    /// <summary>"Paid to date" → "paid_to_date".</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        return s.Length > 0 && char.IsDigit(s[0]) ? "c_" + s : s;
    }

    public static bool IsValid(string key) =>
        key.Length > 0 && (char.IsLetter(key[0]) || key[0] == '_') && key.All(c => char.IsLetterOrDigit(c) || c == '_');
}

/// <summary>A reference to a column of another table. Grain follows the chosen column; measure and basis are optional checks.</summary>
public sealed class RequirementDraft
{
    public TableId? Table { get; set; }
    /// <summary>Null = always the latest version (floating).</summary>
    public int? PinnedVersion { get; set; }
    public string Column { get; set; } = "";
    public GrainKind Grain { get; set; } = GrainKind.DevelopmentCell;
    public MeasureKind? Measure { get; set; }
    public BasisKind? Basis { get; set; }

    public static RequirementDraft From(InputRequirement r) => new()
    {
        Table = r.Source.Table, PinnedVersion = r.Source.Version?.Value, Column = r.ColumnKey,
        Grain = r.Grain, Measure = r.Measure, Basis = r.Basis
    };

    public static RequirementDraft To(TableId table, ColumnSchema c) => new()
    {
        Table = table, Column = c.Key, Grain = c.Grain, Measure = c.Measure, Basis = c.Basis == BasisKind.NotApplicable ? null : c.Basis
    };

    public RequirementDraft Clone() => (RequirementDraft)MemberwiseClone();

    /// <summary>Picks a column of the source and takes its shape as the requirement.</summary>
    public void UseColumn(ColumnSchema c)
    {
        Column = c.Key;
        Grain = c.Grain;
        Measure = c.Measure;
        Basis = c.Basis == BasisKind.NotApplicable ? null : c.Basis;
    }

    public TableRef? Ref => Table is { } t ? new TableRef(t, PinnedVersion is { } v ? new TableVersion(v) : null) : null;

    public InputRequirement? Build(string what, List<string> problems)
    {
        if (Table is not { } t) { problems.Add($"{what}: choose a table."); return null; }
        if (string.IsNullOrWhiteSpace(Column)) { problems.Add($"{what}: choose a column."); return null; }
        return new InputRequirement(Ref!.Value, Column, Grain, Measure, Basis);
    }
}

public sealed class StatisticDraft
{
    public string Key { get; set; } = "";
    public StatisticKind Kind { get; set; } = StatisticKind.VolumeWeightedAverage;
    /// <summary>Most recent exposure periods; null = all.</summary>
    public int? Window { get; set; }
    /// <summary>While true, the key follows kind and window.</summary>
    public bool AutoKey { get; set; } = true;

    public static string SuggestKey(StatisticKind kind, int? window) => kind switch
    {
        StatisticKind.Latest => "latest",
        StatisticKind.Custom => "judgement",
        _ => (kind switch { StatisticKind.SimpleAverage => "sa", StatisticKind.VolumeWeightedAverage => "vwa", _ => "medial" })
             + "_" + (window is { } w ? w.ToString() : "all")
    };

    public static bool UsesWindow(StatisticKind k) => k is StatisticKind.SimpleAverage or StatisticKind.VolumeWeightedAverage or StatisticKind.Medial;

    public static StatisticDraft New(StatisticKind kind, int? window) => new() { Kind = kind, Window = window, Key = SuggestKey(kind, window) };

    public static StatisticDraft From(StatisticSpec s) => new()
    {
        Key = s.Key, Kind = s.Kind, Window = s.WindowSize, AutoKey = s.Key == SuggestKey(s.Kind, s.WindowSize)
    };

    public void Refresh()
    {
        if (!UsesWindow(Kind)) Window = null;
        if (AutoKey) Key = SuggestKey(Kind, Window);
    }

    public StatisticSpec Build() => new(Key.Trim(), Kind, UsesWindow(Kind) ? Window : null);
}

/// <summary>An output column of an input table.</summary>
public sealed class SchemaColumnDraft
{
    public string Key { get; set; } = "";
    public MeasureKind Measure { get; set; } = MeasureKind.Other;
    public BasisKind Basis { get; set; } = BasisKind.Cumulative;
    public GrainKind Grain { get; set; } = GrainKind.DevelopmentCell;

    public static SchemaColumnDraft From(ColumnSchema c) => new() { Key = c.Key, Measure = c.Measure, Basis = c.Basis, Grain = c.Grain };
    public ColumnSchema Build() => new(Key.Trim(), Measure, Basis, Grain);
}

public sealed class MethodColumnDraft
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public MeasureKind Measure { get; set; } = MeasureKind.Other;
    public BasisKind Basis { get; set; } = BasisKind.NotApplicable;
    public bool IsFormula { get; set; } = true;
    public string Expression { get; set; } = "";
    public List<RequirementDraft> Refs { get; set; } = new();
    public bool AutoKey { get; set; } = true;

    public static MethodColumnDraft From(MethodColumn c) => new()
    {
        Key = c.Key, Label = c.Label, Measure = c.Measure, Basis = c.Basis, AutoKey = false,
        IsFormula = c.Source is FormulaColumn,
        Expression = c.Source is FormulaColumn f ? f.Expression : "",
        Refs = c.Source is FormulaColumn g ? g.Refs.Select(RequirementDraft.From).ToList() : new()
    };

    public void LabelChanged()
    {
        if (AutoKey) Key = Keys.Slug(Label);
    }
}

/// <summary>One selectable method of a selection table. The ultimate column comes from the method table itself.</summary>
public sealed class MethodChoiceDraft
{
    public TableId Table { get; set; }
    public int? PinnedVersion { get; set; }
}

public sealed class TableDraft
{
    public TableKind Kind { get; init; }
    public TableId Id { get; init; }
    public int Version { get; init; }
    public bool IsNew => Version == 1;
    public string Name { get; set; } = "";

    // Input
    public bool FromData { get; set; } = true;
    public string ClaimField { get; set; } = "paid";
    public string Expression { get; set; } = "";
    public List<RequirementDraft> FormulaRefs { get; set; } = new();
    public List<SchemaColumnDraft> Columns { get; set; } = new();

    // Development + exhibit
    public List<StatisticDraft> Statistics { get; set; } = new();

    // Development
    public RequirementDraft Source { get; set; } = new() { Grain = GrainKind.DevelopmentCell, Basis = BasisKind.Cumulative };
    public bool IncludeTail { get; set; } = true;

    // Exhibit
    public RequirementDraft Values { get; set; } = new();
    public RequirementDraft? Weights { get; set; }
    public MeasureKind OutputMeasure { get; set; } = MeasureKind.Other;
    public BasisKind OutputBasis { get; set; } = BasisKind.NotApplicable;

    // Method
    public List<MethodColumnDraft> MethodColumns { get; set; } = new();
    public string UltimateKey { get; set; } = "";

    // Selection
    public List<RequirementDraft> SelectionInputs { get; set; } = new();
    public bool Arithmetic { get; set; } = true;
    public bool Weighted { get; set; }
    public List<MethodChoiceDraft> Methods { get; set; } = new();

    public static readonly IReadOnlyList<string> ClaimFields = new[] { "paid", "incurred", "reported_count", "premium" };

    public static (MeasureKind Measure, BasisKind Basis, GrainKind Grain) FieldShape(string field) => field switch
    {
        "paid" => (MeasureKind.Paid, BasisKind.Cumulative, GrainKind.DevelopmentCell),
        "incurred" => (MeasureKind.Incurred, BasisKind.Cumulative, GrainKind.DevelopmentCell),
        "reported_count" => (MeasureKind.ClaimCount, BasisKind.Cumulative, GrainKind.DevelopmentCell),
        _ => (MeasureKind.Premium, BasisKind.NotApplicable, GrainKind.ExposurePeriod)
    };

    /// <summary>Formula input tables have exactly one column; data tables may expose several views of the field.</summary>
    public void SourceChanged()
    {
        if (!FromData && Columns.Count > 1) Columns.RemoveRange(1, Columns.Count - 1);
        if (Columns.Count == 0) Columns.Add(new SchemaColumnDraft());
    }

    /// <summary>A data field implies the shape of its column.</summary>
    public void ClaimFieldChanged()
    {
        var (m, b, g) = FieldShape(ClaimField);
        if (Columns.Count == 0) Columns.Add(new SchemaColumnDraft());
        var c = Columns[0];
        if (string.IsNullOrWhiteSpace(c.Key) || ClaimFields.Contains(c.Key)) c.Key = ClaimField;
        (c.Measure, c.Basis, c.Grain) = (m, b, g);
    }

    // ───────────── Factories ─────────────

    /// <summary>The starting point for the next version of a published table.</summary>
    public static TableDraft Edit(TableDefinition latest, TableVersion next)
    {
        var d = new TableDraft { Kind = TableKinds.Of(latest), Id = latest.Id, Version = next.Value, Name = latest.Name };
        switch (latest)
        {
            case InputTableDefinition i:
                d.Columns = i.Schema.Columns.Select(SchemaColumnDraft.From).ToList();
                if (i.Source is DataSource ds) { d.FromData = true; d.ClaimField = ds.ClaimField; }
                else if (i.Source is FormulaSource fs)
                {
                    d.FromData = false; d.Expression = fs.Expression;
                    d.FormulaRefs = fs.Refs.Select(RequirementDraft.From).ToList();
                }
                break;
            case DevelopmentExhibitDefinition dev:
                d.Statistics = dev.Statistics.Select(StatisticDraft.From).ToList();
                d.Source = new RequirementDraft
                {
                    Table = dev.Source.Table, PinnedVersion = dev.Source.Version?.Value, Column = dev.SourceColumnKey,
                    Grain = GrainKind.DevelopmentCell, Basis = BasisKind.Cumulative
                };
                d.IncludeTail = dev.IncludeTailFactor;
                break;
            case ExhibitTableDefinition ex:
                d.Statistics = ex.Statistics.Select(StatisticDraft.From).ToList();
                if (ex.Inputs.Count > 0) d.Values = RequirementDraft.From(ex.Inputs[0]);
                if (ex.Inputs.Count > 1) d.Weights = RequirementDraft.From(ex.Inputs[1]);
                var sel = ex.Schema.Find(ExhibitDefinition.SelectedColumn) ?? ex.Schema.Columns.FirstOrDefault();
                if (sel is not null) (d.OutputMeasure, d.OutputBasis) = (sel.Measure, sel.Basis);
                break;
            case MethodTableDefinition m:
                d.MethodColumns = m.Columns.Select(MethodColumnDraft.From).ToList();
                d.UltimateKey = m.UltimateColumnKey;
                break;
            case SelectionTableDefinition s:
                d.SelectionInputs = s.Inputs.Select(RequirementDraft.From).ToList();
                d.Arithmetic = s.Averages.Contains(AverageKind.Arithmetic);
                d.Weighted = s.Averages.Contains(AverageKind.Weighted);
                d.Methods = s.SelectableMethods.Select(x => new MethodChoiceDraft { Table = x.Table.Table, PinnedVersion = x.Table.Version?.Value }).ToList();
                break;
        }
        return d;
    }

    /// <summary>A new table of the given kind, prefilled from what the library offers so it is close to publishable.</summary>
    public static TableDraft New(TableKind kind, Library library)
    {
        var latest = library.TableIds.Select(id => library.FindLatest(id)!).OrderBy(TableKinds.Of).ThenBy(t => t.Name).ToList();
        RequirementDraft? First(Func<ColumnSchema, bool> match)
        {
            foreach (var t in latest)
                if (t.Output.Columns.FirstOrDefault(match) is { } c) return RequirementDraft.To(t.Id, c);
            return null;
        }
        var cumulative = First(c => c.Grain == GrainKind.DevelopmentCell && c.Basis == BasisKind.Cumulative && c.Measure == MeasureKind.Paid)
                         ?? First(c => c.Grain == GrainKind.DevelopmentCell && c.Basis == BasisKind.Cumulative);
        var premium = First(c => c.Grain == GrainKind.ExposurePeriod && c.Measure == MeasureKind.Premium);

        var d = new TableDraft { Kind = kind, Id = TableId.New(), Version = 1 };
        switch (kind)
        {
            case TableKind.Input:
                d.Name = "New input table";
                d.FromData = true;
                d.ClaimFieldChanged();
                break;
            case TableKind.Development:
                d.Name = "New development exhibit";
                if (cumulative is not null) d.Source = cumulative;
                d.Statistics = new() { StatisticDraft.New(StatisticKind.VolumeWeightedAverage, null), StatisticDraft.New(StatisticKind.VolumeWeightedAverage, 5), StatisticDraft.New(StatisticKind.SimpleAverage, 5), StatisticDraft.New(StatisticKind.Latest, null) };
                break;
            case TableKind.Exhibit:
                d.Name = "New exhibit";
                if (First(c => c.Grain == GrainKind.ExposurePeriod && c.Measure != MeasureKind.Premium) is { } v) d.Values = v;
                d.Statistics = new() { StatisticDraft.New(StatisticKind.SimpleAverage, null), StatisticDraft.New(StatisticKind.Medial, null), StatisticDraft.New(StatisticKind.Custom, null) };
                break;
            case TableKind.Method:
                d.Name = "New method";
                d.MethodColumns = new();
                if (premium is not null)
                    d.MethodColumns.Add(new MethodColumnDraft { Key = "premium", Label = "Premium", Measure = MeasureKind.Premium, Expression = "$1", Refs = { premium }, AutoKey = false });
                d.MethodColumns.Add(new MethodColumnDraft { Key = "factor", Label = "Factor", IsFormula = false, AutoKey = false });
                d.MethodColumns.Add(new MethodColumnDraft
                {
                    Key = "ultimate", Label = "Ultimate", Measure = MeasureKind.Ultimate, AutoKey = false,
                    Expression = premium is not null ? "premium * factor" : "factor"
                });
                d.UltimateKey = "ultimate";
                break;
            case TableKind.Selection:
                d.Name = "New selection";
                if (premium is not null) d.SelectionInputs.Add(premium);
                d.Methods = latest.OfType<MethodTableDefinition>().Take(2).Select(m => new MethodChoiceDraft { Table = m.Id }).ToList();
                break;
        }
        return d;
    }

    // ───────────── Build ─────────────

    /// <summary>Builds the definition, or returns the form problems that prevent it (missing choices, bad keys).</summary>
    public (TableDefinition? Definition, IReadOnlyList<string> Problems) Build(Func<TableRef, TableDefinition?> resolve)
    {
        var p = new List<string>();
        var id = Id;
        var v = new TableVersion(Version);
        var name = Name.Trim();
        if (name.Length == 0) p.Add("Give the table a name.");

        void Key(string what, string key)
        {
            if (!Keys.IsValid(key.Trim())) p.Add($"{what}: '{key}' is not a valid key (letters, digits and _; not starting with a digit).");
        }

        TableDefinition? def = null;
        switch (Kind)
        {
            case TableKind.Input:
            {
                foreach (var c in Columns) Key("Column", c.Key);
                var schema = new TableSchema(Columns.Select(c => c.Build()).ToList());
                if (FromData)
                    def = new InputTableDefinition(id, v, name, new DataSource(ClaimField, FieldShape(ClaimField).Measure.ToString()), schema);
                else
                {
                    var refs = FormulaRefs.Select((r, i) => r.Build($"Reference ${i + 1}", p)).ToList();
                    if (refs.All(r => r is not null))
                        def = new InputTableDefinition(id, v, name, new FormulaSource(Expression.Trim(), refs.Select(r => r!).ToList()), schema);
                }
                break;
            }
            case TableKind.Development:
            {
                foreach (var s in Statistics) Key("Statistic", s.Key);
                if (Source.Table is null) p.Add("Choose the triangle to develop.");
                else if (string.IsNullOrWhiteSpace(Source.Column)) p.Add("Choose the column to develop.");
                else def = new DevelopmentExhibitDefinition(id, v, name, Source.Ref!.Value, Source.Column,
                    Statistics.Select(s => s.Build()).ToList(), IncludeTail);
                break;
            }
            case TableKind.Exhibit:
            {
                foreach (var s in Statistics) Key("Statistic", s.Key);
                var inputs = new List<InputRequirement?> { Values.Build("Values", p) };
                if (Weights is not null) inputs.Add(Weights.Build("Weights", p));
                if (inputs.All(i => i is not null))
                {
                    var list = inputs.Select(i => i!).ToList();
                    var grain = list[0].Grain switch
                    {
                        GrainKind.DevelopmentCell => GrainKind.DevelopmentPeriod,
                        _ => GrainKind.Scalar
                    };
                    var stats = Statistics.Select(s => s.Build()).ToList();
                    var schema = new TableSchema(stats.Select(s => s.Key).Append(ExhibitDefinition.SelectedColumn).Distinct()
                        .Select(k => new ColumnSchema(k, OutputMeasure, OutputBasis, grain)).ToList());
                    def = new ExhibitTableDefinition(id, v, name, list, stats, schema);
                }
                break;
            }
            case TableKind.Method:
            {
                var cols = new List<MethodColumn>();
                foreach (var c in MethodColumns)
                {
                    Key("Column", c.Key);
                    var label = string.IsNullOrWhiteSpace(c.Label) ? c.Key.Trim() : c.Label.Trim();
                    if (!c.IsFormula) { cols.Add(new MethodColumn(c.Key.Trim(), label, new UserInputColumn(), c.Measure, c.Basis)); continue; }
                    var refs = c.Refs.Select((r, i) => r.Build($"Column '{c.Key}', reference ${i + 1}", p)).ToList();
                    if (refs.All(r => r is not null))
                        cols.Add(new MethodColumn(c.Key.Trim(), label, new FormulaColumn(c.Expression.Trim(), refs.Select(r => r!).ToList()), c.Measure, c.Basis));
                }
                if (string.IsNullOrWhiteSpace(UltimateKey)) p.Add("Mark one column as the ultimate.");
                if (cols.Count == MethodColumns.Count) def = new MethodTableDefinition(id, v, name, cols, UltimateKey.Trim());
                break;
            }
            case TableKind.Selection:
            {
                var inputs = SelectionInputs.Select((r, i) => r.Build($"Shown input {i + 1}", p)).ToList();
                var methods = new List<MethodRef>();
                foreach (var m in Methods)
                {
                    var r = new TableRef(m.Table, m.PinnedVersion is { } pv ? new TableVersion(pv) : null);
                    if (resolve(r) is MethodTableDefinition mt) methods.Add(new MethodRef(r, mt.UltimateColumnKey));
                    else p.Add("A selectable method is not a method table in the library.");
                }
                var averages = new List<AverageKind>();
                if (Arithmetic) averages.Add(AverageKind.Arithmetic);
                if (Weighted) averages.Add(AverageKind.Weighted);
                if (inputs.All(i => i is not null))
                    def = new SelectionTableDefinition(id, v, name, inputs.Select(i => i!).ToList(), averages, methods);
                break;
            }
        }
        return (p.Count == 0 ? def : null, p);
    }

    /// <summary>Form problems, then the definition's own consistency rules. Cross-table checks are left to publish.</summary>
    public IReadOnlyList<string> Check(Func<TableRef, TableDefinition?> resolve)
    {
        var (def, problems) = Build(resolve);
        return def is null ? problems : def.SelfCheck().ToList();
    }

    /// <summary>The grain a formula yields, or the parse/grain problem. Null when the text is empty.</summary>
    public static (GrainKind? Grain, string? Problem) Inspect(string expression, IEnumerable<RequirementDraft> refs, Func<string, GrainKind?> column)
    {
        if (string.IsNullOrWhiteSpace(expression)) return (null, "Write a formula.");
        var reqs = refs.Select(r => new InputRequirement(r.Ref ?? TableRef.Floating(default), r.Column, r.Grain)).ToList();
        try { return (Formula.InferGrain(Formula.Parse(expression), reqs, column), null); }
        catch (FormulaException e) { return (null, e.Message); }
    }
}
