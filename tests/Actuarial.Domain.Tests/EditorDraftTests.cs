using Actuarial.Application.Editing;
using Actuarial.Application.Persistence;
using Actuarial.Application.Seed;

namespace Actuarial.Domain.Tests;

/// <summary>The forms behind the library and template editors.</summary>
public class EditorDraftTests
{
    private static string J<T>(T v) => Json.Serialize(v);

    [Fact]
    public void Every_published_table_round_trips_through_its_form()
    {
        var f = new Fixture();
        var lib = f.Svc.Library;
        foreach (var id in lib.TableIds)
        {
            var latest = lib.FindLatest(id)!;
            var draft = f.Svc.EditTable(id);
            var batch = new TableBatch(lib);
            batch.Drafts.Add(draft);

            var (defs, problems) = batch.Build();
            Assert.Empty(problems);
            Assert.Equal(J(latest with { Version = lib.NextVersion(id) }), J(defs!.Single()));
        }
    }

    [Fact]
    public void An_unchanged_form_publishes_the_next_version()
    {
        var f = new Fixture();
        var batch = new TableBatch(f.Svc.Library);
        batch.Drafts.Add(f.Svc.EditTable(DemoSeed.Bf));
        Assert.True(f.Svc.PublishTables(batch).Ok);
        Assert.Equal(2, f.Svc.Library.Latest(DemoSeed.Bf)!.Value.Value);
    }

    [Theory]
    [InlineData(TableKind.Input)]
    [InlineData(TableKind.Development)]
    [InlineData(TableKind.Exhibit)]
    [InlineData(TableKind.Method)]
    [InlineData(TableKind.Selection)]
    public void A_new_table_form_starts_publishable(TableKind kind)
    {
        var f = new Fixture();
        var batch = new TableBatch(f.Svc.Library);
        var d = f.Svc.NewTable(kind);
        batch.Drafts.Add(d);

        Assert.Empty(d.Check(batch.Resolve));
        var r = f.Svc.PublishTables(batch);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
        Assert.Equal(1, f.Svc.Library.Latest(d.Id)!.Value.Value);
    }

    [Fact]
    public void Incomplete_forms_say_what_is_missing()
    {
        var f = new Fixture();
        var d = f.Svc.NewTable(TableKind.Development);
        d.Name = " ";
        d.Source = new RequirementDraft();
        d.Statistics.Add(new StatisticDraft { Key = "9bad", AutoKey = false });

        var problems = d.Check(new TableBatch(f.Svc.Library).Resolve);
        Assert.Contains(problems, p => p.Contains("name"));
        Assert.Contains(problems, p => p.Contains("triangle to develop"));
        Assert.Contains(problems, p => p.Contains("'9bad' is not a valid key"));
    }

    [Fact]
    public void Live_check_reports_the_domain_rules()
    {
        var f = new Fixture();
        var d = f.Svc.EditTable(DemoSeed.ExpectedClaims);
        d.UltimateKey = "premium";
        Assert.Contains(d.Check(new TableBatch(f.Svc.Library).Resolve), p => p.Contains("must have measure Ultimate"));
    }

    [Fact]
    public void Statistic_keys_follow_kind_and_window_until_typed()
    {
        var s = StatisticDraft.New(StatisticKind.SimpleAverage, 3);
        Assert.Equal("sa_3", s.Key);
        s.Kind = StatisticKind.VolumeWeightedAverage; s.Window = null; s.Refresh();
        Assert.Equal("vwa_all", s.Key);
        s.Key = "mine"; s.AutoKey = false;
        s.Kind = StatisticKind.Latest; s.Window = 4; s.Refresh();
        Assert.Equal("mine", s.Key);
        Assert.Null(s.Window);
    }

    [Fact]
    public void A_batch_publishes_a_new_table_with_a_table_that_reads_it()
    {
        var f = new Fixture();
        var batch = new TableBatch(f.Svc.Library);

        var method = f.Svc.NewTable(TableKind.Method);
        method.Name = "Load method";
        batch.Drafts.Add(method);

        var selection = f.Svc.EditTable(DemoSeed.Selection);
        selection.Methods.Add(new MethodChoiceDraft { Table = method.Id });
        batch.Drafts.Add(selection);

        Assert.Contains(batch.Sources(selection), s => s.Id == method.Id && s.InBatch);
        var r = f.Svc.PublishTables(batch);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
        var published = (SelectionTableDefinition)f.Svc.Library.FindLatest(DemoSeed.Selection)!;
        Assert.True(published.OffersMethod(method.Id));
    }

    [Fact]
    public void Columns_of_an_edited_table_come_from_its_draft()
    {
        var f = new Fixture();
        var batch = new TableBatch(f.Svc.Library);
        var bf = f.Svc.EditTable(DemoSeed.Bf);
        bf.MethodColumns.Add(new MethodColumnDraft { Key = "extra", Label = "Extra", IsFormula = false });
        batch.Drafts.Add(bf);

        Assert.Contains(batch.ColumnsOf(DemoSeed.Bf, null), c => c.Key == "extra");
        Assert.DoesNotContain(batch.ColumnsOf(DemoSeed.Bf, 1), c => c.Key == "extra");
    }

    [Fact]
    public void Breaking_change_offers_its_dependents()
    {
        var f = new Fixture();
        var batch = new TableBatch(f.Svc.Library);
        batch.Drafts.Add(f.Svc.EditTable(DemoSeed.LossRatios));
        Assert.Contains(batch.Dependents(), t => t.Id == DemoSeed.Bf);
    }

    [Fact]
    public void Templates_round_trip_through_their_form()
    {
        var f = new Fixture();
        foreach (var id in f.Svc.Templates.TemplateIds)
        {
            var latest = f.Svc.Templates.FindLatest(id)!;
            var (t, problems) = f.Svc.EditTemplate(id).Build();
            Assert.Empty(problems);
            Assert.Equal(J(latest with { Version = f.Svc.Templates.NextVersion(id) }), J(t));
        }
    }

    [Fact]
    public void A_template_built_in_the_form_publishes()
    {
        var f = new Fixture();
        var d = f.Svc.NewTemplate();
        d.Name = "Quick look";
        Assert.Contains(d.Build().Problems, p => p.Contains("at least one object"));

        var folder = TemplateNodeDraft.Folder("Paid");
        var col = TemplateNodeDraft.Collection("Chain ladder");
        col.Children.Add(TemplateNodeDraft.Object(f.Svc.Library.FindLatest(DemoSeed.PaidCl)!));
        col.Children.Add(new TemplateNodeDraft { Kind = TemplateNodeKind.Object, Name = "Pinned paid", Table = DemoSeed.Paid, PinnedVersion = 1 });
        folder.Children.Add(col);
        d.Root.Children.Add(folder);

        var r = f.Svc.PublishTemplate(d);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var t = f.Svc.Templates.FindLatest(d.Id)!;
        Assert.Equal("Quick look", t.Root.Name);
        Assert.Equal(new TableVersion(1), t.Objects().Single(o => o.Name == "Pinned paid").Table.Version);
    }

    [Fact]
    public void An_object_without_a_table_is_a_form_problem()
    {
        var f = new Fixture();
        var d = f.Svc.NewTemplate();
        d.Root.Children[0].Children.Add(new TemplateNodeDraft { Kind = TemplateNodeKind.Object, Name = "Empty" });
        var r = f.Svc.PublishTemplate(d);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("choose a table"));
    }

    [Theory]
    [InlineData("Paid to date", "paid_to_date")]
    [InlineData("  Loss ratio (%) ", "loss_ratio")]
    [InlineData("12 month factor", "c_12_month_factor")]
    public void Keys_are_slugged_from_labels(string label, string key) => Assert.Equal(key, Keys.Slug(label));
}
