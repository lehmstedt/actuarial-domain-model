using Actuarial.Application.Seed;

namespace Actuarial.Domain.Tests;

public class LibraryPublishTests
{
    private static Library Seeded()
    {
        var lib = new Library();
        Assert.True(lib.Publish(DemoSeed.LibraryV1()).Ok);
        return lib;
    }

    private static string Errors(PublishResult r) => string.Join(" | ", r.Errors.Select(e => e.Message));

    [Fact]
    public void Missing_column_is_refused_and_nothing_is_committed()
    {
        var lib = Seeded();
        var paid = (InputTableDefinition)lib.FindLatest(DemoSeed.Paid)!;
        var renamed = paid with
        {
            Version = new TableVersion(2),
            Schema = new TableSchema(new[] { new ColumnSchema("paid_amount", MeasureKind.Paid, BasisKind.Cumulative, GrainKind.DevelopmentCell) })
        };

        var r = lib.Publish(new[] { renamed });

        Assert.False(r.Ok);
        Assert.Contains("has no column 'paid'", Errors(r));
        Assert.Contains("floating ref", Errors(r));
        Assert.Equal(new TableVersion(1), lib.Latest(DemoSeed.Paid));
    }

    [Theory]
    [InlineData(GrainKind.ExposurePeriod, MeasureKind.Paid, BasisKind.Cumulative, "expects grain")]
    [InlineData(GrainKind.DevelopmentCell, MeasureKind.Incurred, BasisKind.Cumulative, "expects measure")]
    [InlineData(GrainKind.DevelopmentCell, MeasureKind.Paid, BasisKind.Incremental, "expects basis")]
    public void Grain_measure_and_basis_mismatches_are_refused(GrainKind g, MeasureKind m, BasisKind b, string expected)
    {
        var lib = Seeded();
        var paid = (InputTableDefinition)lib.FindLatest(DemoSeed.Paid)!;
        var changed = paid with { Version = new TableVersion(2), Schema = new TableSchema(new[] { new ColumnSchema("paid", m, b, g) }) };
        var r = lib.Publish(new[] { changed });
        Assert.False(r.Ok);
        Assert.Contains(expected, Errors(r));
    }

    [Fact]
    public void A_cycle_is_refused()
    {
        var lib = Seeded();
        var paid = (InputTableDefinition)lib.FindLatest(DemoSeed.Paid)!;
        var cyclic = paid with
        {
            Version = new TableVersion(2),
            Source = new FormulaSource("$1", new[] { new InputRequirement(TableRef.Floating(DemoSeed.CaseReserves), "case", GrainKind.DevelopmentCell) })
        };
        var r = lib.Publish(new[] { cyclic });
        Assert.False(r.Ok);
        Assert.Contains("cycle", Errors(r));
    }

    [Fact]
    public void Versions_are_immutable_and_allocated_in_sequence()
    {
        var lib = Seeded();
        var paid = lib.FindLatest(DemoSeed.Paid)!;
        Assert.Contains("already exists", Errors(lib.Publish(new[] { paid })));
        Assert.Contains("next version", Errors(lib.Publish(new[] { paid with { Version = new TableVersion(5) } })));
        Assert.Equal(new TableVersion(2), lib.NextVersion(DemoSeed.Paid));
        Assert.True(lib.Publish(new[] { paid with { Version = lib.NextVersion(DemoSeed.Paid) } }).Ok);
    }

    [Fact]
    public void A_breaking_change_published_with_its_dependents_is_accepted_atomically()
    {
        var lib = Seeded();
        var paid = (InputTableDefinition)lib.FindLatest(DemoSeed.Paid)!;
        var renamed = paid with
        {
            Version = new TableVersion(2),
            Schema = new TableSchema(new[] { new ColumnSchema("amount", MeasureKind.Paid, BasisKind.Cumulative, GrainKind.DevelopmentCell) })
        };
        static InputRequirement Fix(InputRequirement r) => r.Source.Table == DemoSeed.Paid ? r with { ColumnKey = "amount" } : r;
        static MethodTableDefinition FixMethod(MethodTableDefinition m) => m with
        {
            Version = new TableVersion(m.Version.Value + 1),
            Columns = m.Columns.Select(c => c.Source is FormulaColumn f ? c with { Source = f with { Refs = f.Refs.Select(Fix).ToList() } } : c).ToList()
        };

        var dependents = new List<TableDefinition>
        {
            ((InputTableDefinition)lib.FindLatest(DemoSeed.CaseReserves)!) is var cr
                ? cr with { Version = new TableVersion(2), Source = new FormulaSource("$1 - $2", ((FormulaSource)cr.Source).Refs.Select(Fix).ToList()) } : null!,
            ((DevelopmentExhibitDefinition)lib.FindLatest(DemoSeed.PaidDev)!) with { Version = new TableVersion(2), SourceColumnKey = "amount" },
            FixMethod((MethodTableDefinition)lib.FindLatest(DemoSeed.PaidCl)!),
            FixMethod((MethodTableDefinition)lib.FindLatest(DemoSeed.Bf)!),
            FixMethod((MethodTableDefinition)lib.FindLatest(DemoSeed.Summary)!),
        };

        var partial = lib.Publish(new TableDefinition[] { renamed, dependents[0] });
        Assert.False(partial.Ok);
        Assert.Equal(new TableVersion(1), lib.Latest(DemoSeed.CaseReserves));   // atomic rollback

        var all = lib.Publish(dependents.Prepend(renamed).ToList());
        Assert.True(all.Ok, Errors(all));
        Assert.Equal(new TableVersion(2), lib.Latest(DemoSeed.Paid));
    }

    [Fact]
    public void Formulas_are_checked_at_publish()
    {
        var lib = Seeded();
        var m = (MethodTableDefinition)lib.FindLatest(DemoSeed.PaidCl)!;
        MethodTableDefinition With(string key, string expr) => m with
        {
            Version = new TableVersion(2),
            Columns = m.Columns.Select(c => c.Key == key ? c with { Source = ((FormulaColumn)c.Source) with { Expression = expr } } : c).ToList()
        };

        Assert.Contains("Unknown column", Errors(lib.Publish(new[] { With("ultimate", "latest_paid * cfd") })));
        Assert.Contains("do not fit", Errors(lib.Publish(new[] { With("latest_paid", "$1") })));                  // a triangle in a per-period column
        Assert.Contains("cycle", Errors(lib.Publish(new[] { With("latest_paid", "ultimate / cdf") })));
        Assert.Contains("$2 does not exist", Errors(lib.Publish(new[] { With("premium", "$2") })));
        Assert.Contains("Unexpected", Errors(lib.Publish(new[] { With("premium", "$1 +* 2") })));
    }

    [Fact]
    public void Selection_tables_may_only_select_method_ultimates()
    {
        var lib = Seeded();
        var sel = (SelectionTableDefinition)lib.FindLatest(DemoSeed.Selection)!;
        SelectionTableDefinition Adding(TableId t, string col) => sel with
        {
            Version = new TableVersion(2),
            SelectableMethods = sel.SelectableMethods.Append(new MethodRef(TableRef.Floating(t), col)).ToList()
        };

        // A method table downstream of the selection would close a loop.
        Assert.Contains(lib.Publish(new[] { Adding(DemoSeed.Summary, "selected") }).Errors, e => e.Message.Contains("cycle"));
        // A column that is not the method's designated ultimate.
        Assert.Contains(lib.Publish(new[] { Adding(DemoSeed.PaidCl, "premium") }).Errors, e => e.Message.Contains("designates 'ultimate'"));
    }
}
