using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Actuarial.Application.Data;
using Actuarial.Domain;
using Actuarial.Domain.Formulas;

namespace Actuarial.Application.Calculation;

/// <summary>
/// Deterministic engine: results are a pure function of manifest, structure, valuation date, data snapshot,
/// active state and this engine's version. Tables are computed in dependency order.
/// </summary>
public sealed class CalculationEngine : ICalculationEngine
{
    public const string CurrentVersion = "engine-1.0.0";
    public string Version => CurrentVersion;

    private readonly Func<DataSnapshot, SnapshotData> _data;

    public CalculationEngine(Func<DataSnapshot, SnapshotData> data) => _data = data;

    public ResultSnapshot Compute(Analysis analysis, Library library)
    {
        var ctx = new Context(analysis, library, _data(analysis.Data));
        foreach (var id in analysis.Manifest.TopologicalOrder(library))
        {
            var def = library.Get(id, analysis.Manifest.Pinned[id]);
            TableResult result;
            try
            {
                result = def switch
                {
                    InputTableDefinition t => ctx.Input(t),
                    DevelopmentExhibitDefinition t => ctx.Development(t),
                    ExhibitTableDefinition t => ctx.Exhibit(t),
                    MethodTableDefinition t => ctx.Method(t),
                    SelectionTableDefinition t => ctx.Selection(t),
                    _ => new TableResult(Array.Empty<ColumnResult>(), new[] { $"No calculation for {def.KindLabel} tables." })
                };
            }
            catch (Exception e) when (e is FormulaException or InvalidOperationException or KeyNotFoundException or OverflowException)
            {
                result = new TableResult(Array.Empty<ColumnResult>(), new[] { e.Message });
            }
            ctx.Results[id] = result;
        }

        var tables = ctx.Results.ToDictionary(kv => kv.Key, kv => kv.Value);
        return new ResultSnapshot(Hash(analysis, tables), Version, tables);
    }

    /// <summary>Canonical hash of inputs and results. Dormant state is excluded (it cannot change a number).</summary>
    public string Hash(Analysis a, IReadOnlyDictionary<TableId, TableResult> tables)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append('\n');
        string N(decimal? d) => d?.ToString(CultureInfo.InvariantCulture) ?? "-";

        Line($"engine|{Version}");
        Line($"valuation|{a.ValuationDate:yyyy-MM-dd}");
        var s = a.Structure;
        Line($"structure|{s.FirstExposureStart:yyyy-MM-dd}|{s.ExposurePeriodCount}|{s.DevelopmentPeriodCount}|{s.ExposurePeriodLength.Months}|{s.DevelopmentPeriodLength.Months}");
        Line($"data|{a.Data.ContentHash}");
        foreach (var (id, v) in a.Manifest.Pinned.OrderBy(p => p.Key.Value))
            Line($"pin|{id.Value:N}|{v.Value}");
        foreach (var line in a.State.Select(kv => $"state|{KeyText(kv.Key)}|{ValueText(kv.Value.Value)}").OrderBy(x => x, StringComparer.Ordinal))
            Line(line);
        foreach (var (id, t) in tables.OrderBy(t => t.Key.Value))
        {
            Line($"table|{id.Value:N}");
            foreach (var c in t.Columns)
                Line($"col|{c.Key}|{c.Grain}|" + string.Join(";", c.Values.Select(r => string.Join(",", r.Select(N)))));
            foreach (var p in t.Problems) Line($"problem|{p}");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    public static string KeyText(StateKey k) =>
        $"{k.Table.Value:N}|{k.ColumnKey}|{(k.Exposure is { } e ? $"{e.Start:yyyy-MM-dd}+{e.LengthMonths}" : "-")}|{(k.Development is { } d ? $"{d.AgeMonths}+{d.LengthMonths}" : "-")}";

    public static string ValueText(StateValue v) => v switch
    {
        SelectedStatistic s => $"stat:{s.StatisticKey}",
        SelectedMethod m => $"method:{m.MethodTable.Value:N}",
        SelectedAverage a => $"average:{a.Average}",
        ManualValue m => $"value:{m.Value.ToString(CultureInfo.InvariantCulture)}",
        _ => v.ToString()!
    };

    // ───────────────────────── per-analysis evaluation context ─────────────────────────

    private sealed class Context
    {
        public readonly Dictionary<TableId, TableResult> Results = new();
        private readonly Analysis _a;
        private readonly Library _lib;
        private readonly SnapshotData _data;
        private readonly List<ExposureKey> _ex;
        private readonly List<DevKey> _dev;
        private readonly int[] _latest;

        private int NE => _ex.Count;
        private int ND => _dev.Count;

        public Context(Analysis a, Library lib, SnapshotData data)
        {
            (_a, _lib, _data) = (a, lib, data);
            _ex = a.Structure.ExposureKeys().ToList();
            _dev = a.Structure.DevelopmentKeys().ToList();
            _latest = _ex.Select(e => a.Structure.LatestDevIndex(e, a.ValuationDate)).ToArray();
        }

        // ----- helpers -----

        private decimal?[][] Shape(GrainKind g) => g switch
        {
            GrainKind.DevelopmentCell => Rows(NE, ND),
            GrainKind.DevelopmentPeriod => Rows(1, ND),
            GrainKind.ExposurePeriod => Rows(NE, 1),
            _ => Rows(1, 1)
        };

        private static decimal?[][] Rows(int r, int c) => Enumerable.Range(0, r).Select(_ => new decimal?[c]).ToArray();

        private StateValue? State(TableId t, string col, ExposureKey? e, DevKey? d) =>
            _a.State.TryGetValue(new StateKey(t, col, e, d), out var s) ? s.Value : null;

        private decimal? Manual(TableId t, string col, ExposureKey? e, DevKey? d) =>
            State(t, col, e, d) is ManualValue m ? m.Value : null;

        private ColumnResult Ref(InputRequirement r)
        {
            if (!Results.TryGetValue(r.Source.Table, out var t))
                throw new InvalidOperationException($"Table {r.Source.Table} has not been computed.");
            return t.Column(r.ColumnKey)
                   ?? throw new InvalidOperationException($"'{_lib.Get(r.Source.Table, _a.Manifest.Pinned[r.Source.Table]).Name}' did not produce column '{r.ColumnKey}'.");
        }

        private string NameOf(TableId id) => _lib.Get(id, _a.Manifest.Pinned[id]).Name;

        /// <summary>Applies manual overrides held in state for a column, cell by cell.</summary>
        private void Overrides(TableId t, string col, GrainKind g, decimal?[][] v)
        {
            for (var e = 0; e < v.Length; e++)
            for (var d = 0; d < v[e].Length; d++)
            {
                ExposureKey? ek = g is GrainKind.DevelopmentCell or GrainKind.ExposurePeriod ? _ex[e] : null;
                DevKey? dk = g is GrainKind.DevelopmentCell or GrainKind.DevelopmentPeriod ? _dev[d] : null;
                if (Manual(t, col, ek, dk) is { } m) v[e][d] = m;
            }
        }

        // ----- input tables -----

        public TableResult Input(InputTableDefinition t)
        {
            var cols = new List<ColumnResult>();
            foreach (var c in t.Schema.Columns)
            {
                var v = t.Source switch
                {
                    DataSource ds => FromData(ds.ClaimField, c),
                    FormulaSource fs => Evaluate(fs.Expression, fs.Refs, c.Grain, _ => null),
                    _ => Shape(c.Grain)
                };
                Overrides(t.Id, c.Key, c.Grain, v);
                cols.Add(new ColumnResult(c.Key, c.Grain, v));
            }
            return new TableResult(cols, Array.Empty<string>());
        }

        private decimal?[][] FromData(string field, ColumnSchema c)
        {
            var firstStart = Months.Index(_a.Structure.FirstExposureStart);
            var eLen = _a.Structure.ExposurePeriodLength.Months;
            var dLen = _a.Structure.DevelopmentPeriodLength.Months;

            if (field == "premium")
            {
                var p = Shape(GrainKind.ExposurePeriod);
                foreach (var cell in _data.Premium)
                {
                    var e = Math.DivRem(cell.AccidentMonth - firstStart, eLen, out _);
                    if (cell.AccidentMonth < firstStart || e >= NE) continue;
                    p[e][0] = (p[e][0] ?? 0) + cell.Amount;
                }
                return p;
            }

            var inc = new decimal[NE, ND];
            var beyond = new decimal[NE];        // movements after the last development period (for exposure totals)
            foreach (var cell in _data.Claims)
            {
                if (cell.AccidentMonth < firstStart) continue;
                var e = (cell.AccidentMonth - firstStart) / eLen;
                if (e >= NE) continue;
                var age = cell.TransactionMonth - Months.Index(_ex[e].Start);
                if (age < 0) age = 0;
                var d = age / dLen;
                var amount = field switch { "paid" => cell.Paid, "incurred" => cell.Incurred, _ => cell.ReportedCount };
                if (d >= ND) beyond[e] += amount; else inc[e, d] += amount;
            }

            if (c.Grain == GrainKind.ExposurePeriod)
            {
                var tot = Shape(GrainKind.ExposurePeriod);
                for (var e = 0; e < NE; e++)
                {
                    decimal s = beyond[e];
                    for (var d = 0; d < ND; d++) s += inc[e, d];
                    tot[e][0] = _latest[e] >= 0 ? s : null;
                }
                return tot;
            }

            var v = Shape(GrainKind.DevelopmentCell);
            for (var e = 0; e < NE; e++)
            {
                decimal cum = 0;
                for (var d = 0; d < ND; d++)
                {
                    cum += inc[e, d];
                    if (d > _latest[e]) continue;
                    v[e][d] = c.Basis == BasisKind.Incremental ? inc[e, d] : cum;
                }
            }
            return v;
        }

        // ----- formulas -----

        private decimal?[][] Evaluate(string expression, IReadOnlyList<InputRequirement> refs, GrainKind target, Func<string, decimal?[][]?> column)
        {
            var expr = Formula.Parse(expression);
            var refValues = refs.Select(Ref).ToList();
            var v = Shape(target);
            for (var e = 0; e < v.Length; e++)
            for (var d = 0; d < v[e].Length; d++)
            {
                if (target == GrainKind.DevelopmentCell && d > _latest[e]) continue;
                v[e][d] = Eval(expr, e, d, refValues, column);
            }
            return v;
        }

        private decimal? Eval(Expr x, int e, int d, IReadOnlyList<ColumnResult> refs, Func<string, decimal?[][]?> column)
        {
            switch (x)
            {
                case NumberExpr n: return n.Value;
                case RefExpr r: return refs[r.Index - 1].At(e, d);
                case ColumnExpr c: return (column(c.Key) ?? throw new FormulaException($"Unknown column '{c.Key}'."))[e][0];
                case NegateExpr n: return -Eval(n.Operand, e, d, refs, column);
                case BinaryExpr b:
                    var l = Eval(b.Left, e, d, refs, column);
                    var rr = Eval(b.Right, e, d, refs, column);
                    if (l is null || rr is null) return null;
                    return b.Op switch
                    {
                        '+' => l + rr,
                        '-' => l - rr,
                        '*' => l * rr,
                        '/' => rr == 0 ? null : l / rr,
                        _ => null
                    };
                case CallExpr call:
                    switch (call.Function)
                    {
                        case "latest":
                        case "current":
                            return _latest[e] < 0 ? null : Eval(call.Args[0], e, _latest[e], refs, column);
                        case "abs":
                            return Eval(call.Args[0], e, d, refs, column) is { } a ? Math.Abs(a) : null;
                        case "coalesce":
                            foreach (var arg in call.Args)
                                if (Eval(arg, e, d, refs, column) is { } v) return v;
                            return null;
                        case "min":
                        case "max":
                            var vals = call.Args.Select(a => Eval(a, e, d, refs, column)).ToList();
                            if (vals.Any(v => v is null)) return null;
                            return call.Function == "min" ? vals.Min() : vals.Max();
                    }
                    break;
            }
            throw new FormulaException("Unsupported expression.");
        }

        // ----- statistics -----

        private decimal? Statistic(StatisticSpec s, IReadOnlyList<(decimal Value, decimal Weight)> points, decimal? custom)
        {
            if (s.Kind == StatisticKind.Custom) return custom;
            if (points.Count == 0) return null;
            var window = s.WindowSize is { } w ? points.TakeLast(w).ToList() : points.ToList();
            switch (s.Kind)
            {
                case StatisticKind.Latest: return points[^1].Value;
                case StatisticKind.SimpleAverage: return window.Average(p => p.Value);
                case StatisticKind.VolumeWeightedAverage:
                    var wsum = window.Sum(p => p.Weight);
                    return wsum == 0 ? null : window.Sum(p => p.Value * p.Weight) / wsum;
                case StatisticKind.Medial:
                    if (window.Count < 3) return window.Average(p => p.Value);
                    var sorted = window.Select(p => p.Value).OrderBy(v => v).ToList();
                    return sorted.Skip(1).Take(sorted.Count - 2).Average();
            }
            return null;
        }

        private decimal? Selected(ExhibitDefinition ex, DevKey? dk, Func<string, decimal?> statValue) =>
            State(ex.Id, ExhibitDefinition.SelectedColumn, null, dk) switch
            {
                SelectedStatistic s => statValue(s.StatisticKey),
                ManualValue m => m.Value,
                _ => null
            };

        // ----- development exhibit -----

        public TableResult Development(DevelopmentExhibitDefinition t)
        {
            var src = Ref(t.Requirements.First());
            var ata = Shape(GrainKind.DevelopmentCell);
            for (var e = 0; e < NE; e++)
            for (var d = 1; d < ND; d++)
                if (src.At(e, d - 1) is { } prev && src.At(e, d) is { } cur && prev != 0)
                    ata[e][d] = cur / prev;

            var stats = new Dictionary<string, decimal?[][]>();
            foreach (var s in t.Statistics)
            {
                var row = Shape(GrainKind.DevelopmentPeriod);
                for (var d = 1; d < ND; d++)
                {
                    var points = new List<(decimal, decimal)>();
                    for (var e = 0; e < NE; e++)
                        if (ata[e][d] is { } f) points.Add((f, src.At(e, d - 1)!.Value));
                    // Volume-weighted = sum(C_d) / sum(C_d-1) over the window, i.e. factors weighted by the earlier cumulative.
                    row[0][d] = Statistic(s, points, Manual(t.Id, s.Key, null, _dev[d]));
                }
                stats[s.Key] = row;
            }

            var selected = Shape(GrainKind.DevelopmentPeriod);
            for (var d = 1; d < ND; d++)
                selected[0][d] = Selected(t, _dev[d], k => stats.TryGetValue(k, out var r) ? r[0][d] : null);

            decimal tail = t.IncludeTailFactor ? Manual(t.Id, DevelopmentExhibitDefinition.TailColumn, null, null) ?? 1m : 1m;
            var cdf = Shape(GrainKind.DevelopmentPeriod);
            decimal? running = tail;
            for (var d = ND - 1; d >= 0; d--)
            {
                cdf[0][d] = running;
                if (d >= 1) running = running is { } r && selected[0][d] is { } f ? r * f : null;
            }

            var cols = t.Statistics.Select(s => new ColumnResult(s.Key, GrainKind.DevelopmentPeriod, stats[s.Key])).ToList();
            cols.Add(new ColumnResult(ExhibitDefinition.SelectedColumn, GrainKind.DevelopmentPeriod, selected));
            cols.Add(new ColumnResult(DevelopmentExhibitDefinition.CdfColumn, GrainKind.DevelopmentPeriod, cdf));
            if (t.IncludeTailFactor)
                cols.Add(new ColumnResult(DevelopmentExhibitDefinition.TailColumn, GrainKind.Scalar, new[] { new decimal?[] { tail } }));
            cols.Add(new ColumnResult("ata", GrainKind.DevelopmentCell, ata, IsOutput: false));

            var missing = Enumerable.Range(1, Math.Max(0, ND - 1)).Count(d => selected[0][d] is null);
            var problems = missing > 0 ? new[] { $"{missing} development period(s) have no selected factor, so the CDF is incomplete." } : Array.Empty<string>();
            return new TableResult(cols, problems);
        }

        // ----- generic exhibit -----

        public TableResult Exhibit(ExhibitTableDefinition t)
        {
            var input = Ref(t.Inputs[0]);
            var weight = t.Inputs.Count > 1 ? Ref(t.Inputs[1]) : null;
            var byDev = t.ReducedGrain == GrainKind.DevelopmentPeriod;
            var g = byDev ? GrainKind.DevelopmentPeriod : GrainKind.Scalar;
            var slots = byDev ? ND : 1;

            var stats = new Dictionary<string, decimal?[][]>();
            foreach (var s in t.Statistics)
            {
                var v = Shape(g);
                for (var d = 0; d < slots; d++)
                {
                    var points = new List<(decimal, decimal)>();
                    for (var e = 0; e < NE; e++)
                        if (input.At(e, d) is { } x) points.Add((x, weight?.At(e, d) ?? 1m));
                    v[0][d] = Statistic(s, points, Manual(t.Id, s.Key, null, byDev ? _dev[d] : null));
                }
                stats[s.Key] = v;
            }

            var selected = Shape(g);
            for (var d = 0; d < slots; d++)
                selected[0][d] = Selected(t, byDev ? _dev[d] : null, k => stats.TryGetValue(k, out var r) ? r[0][d] : null);

            var cols = t.Statistics.Select(s => new ColumnResult(s.Key, g, stats[s.Key])).ToList();
            cols.Add(new ColumnResult(ExhibitDefinition.SelectedColumn, g, selected));
            cols.Add(new ColumnResult("input", input.Grain, input.Values, IsOutput: false));
            if (weight is not null) cols.Add(new ColumnResult("weight", weight.Grain, weight.Values, IsOutput: false));
            return new TableResult(cols, Array.Empty<string>());
        }

        // ----- method table -----

        public TableResult Method(MethodTableDefinition t)
        {
            var values = new Dictionary<string, decimal?[][]>();
            foreach (var c in t.EvaluationOrder())
            {
                var v = c.Source switch
                {
                    FormulaColumn f => Evaluate(f.Expression, f.Refs, GrainKind.ExposurePeriod, k => values.GetValueOrDefault(k)),
                    _ => Shape(GrainKind.ExposurePeriod)
                };
                Overrides(t.Id, c.Key, GrainKind.ExposurePeriod, v);
                values[c.Key] = v;
            }
            var cols = t.Columns.Select(c => new ColumnResult(c.Key, GrainKind.ExposurePeriod, values[c.Key])).ToList();
            return new TableResult(cols, Array.Empty<string>());
        }

        // ----- selection table -----

        public TableResult Selection(SelectionTableDefinition t)
        {
            var ults = t.SelectableMethods.Select(m => (m.Table.Table, Col: Ref(new InputRequirement(m.Table, m.UltimateColumnKey, GrainKind.ExposurePeriod)))).ToList();
            var weights = t.SelectableMethods.ToDictionary(m => m.Table.Table,
                m => Manual(t.Id, SelectionTableDefinition.WeightColumn(m.Table.Table), null, null) ?? 1m);

            var arith = Shape(GrainKind.ExposurePeriod);
            var weighted = Shape(GrainKind.ExposurePeriod);
            var selected = Shape(GrainKind.ExposurePeriod);
            for (var e = 0; e < NE; e++)
            {
                var vals = ults.Where(u => u.Col.At(e, 0) is not null).Select(u => (u.Table, V: u.Col.At(e, 0)!.Value)).ToList();
                if (vals.Count > 0)
                {
                    arith[e][0] = vals.Average(v => v.V);
                    var ws = vals.Sum(v => weights[v.Table]);
                    weighted[e][0] = ws == 0 ? null : vals.Sum(v => v.V * weights[v.Table]) / ws;
                }

                selected[e][0] = State(t.Id, SelectionTableDefinition.SelectedUltimate, _ex[e], null) switch
                {
                    SelectedMethod m => ults.FirstOrDefault(u => u.Table == m.MethodTable).Col?.At(e, 0),
                    SelectedAverage { Average: AverageKind.Arithmetic } => arith[e][0],
                    SelectedAverage { Average: AverageKind.Weighted } => weighted[e][0],
                    ManualValue mv => mv.Value,
                    _ => null
                };
            }

            var cols = new List<ColumnResult> { new(SelectionTableDefinition.SelectedUltimate, GrainKind.ExposurePeriod, selected) };
            if (t.Averages.Contains(AverageKind.Arithmetic))
                cols.Add(new(SelectionTableDefinition.AverageColumn(AverageKind.Arithmetic), GrainKind.ExposurePeriod, arith));
            if (t.Averages.Contains(AverageKind.Weighted))
            {
                cols.Add(new(SelectionTableDefinition.AverageColumn(AverageKind.Weighted), GrainKind.ExposurePeriod, weighted));
                foreach (var m in t.SelectableMethods)
                    cols.Add(new(SelectionTableDefinition.WeightColumn(m.Table.Table), GrainKind.Scalar, new[] { new decimal?[] { weights[m.Table.Table] } }));
            }
            foreach (var u in ults)
                cols.Add(new($"method:{u.Table.Value:N}", GrainKind.ExposurePeriod, u.Col.Values, IsOutput: false));
            for (var i = 0; i < t.Inputs.Count; i++)
            {
                var r = Ref(t.Inputs[i]);
                cols.Add(new($"input:{i + 1}", r.Grain, r.Values, IsOutput: false));
            }

            var missing = selected.Count(r => r[0] is null);
            var problems = missing > 0 ? new[] { $"{missing} exposure period(s) have no selected ultimate." } : Array.Empty<string>();
            return new TableResult(cols, problems);
        }
    }
}
