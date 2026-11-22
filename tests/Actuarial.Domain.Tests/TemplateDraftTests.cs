using Actuarial.Application.Seed;
using Actuarial.Application.UseCases;

namespace Actuarial.Domain.Tests;

public class TemplateDraftTests
{
    [Fact]
    public void Unchanged_draft_rebuilds_the_same_tree()
    {
        var f = new Fixture();
        var latest = f.Svc.Templates.FindLatest(DemoSeed.Standard)!;
        var draft = TemplateDraft.From(f.Svc.DraftNextTemplateVersion(DemoSeed.Standard));
        var built = draft.Build();

        Assert.Equal(latest.Version.Value + 1, built.Version.Value);
        Assert.Equal(latest.Objects().Select(o => (o.NodeId, o.Name, o.Table)), built.Objects().Select(o => (o.NodeId, o.Name, o.Table)));
        Assert.True(f.Svc.PublishTemplate(built).Ok);
    }

    [Fact]
    public void New_template_is_built_and_published_from_the_editor_operations()
    {
        var f = new Fixture();
        var paid = f.Svc.Library.FindLatest(DemoSeed.Paid)!;
        var draft = TemplateDraft.New("Paid view");
        Assert.Contains(draft.Problems(), p => p.Contains("at least one table"));

        var col = draft.Root.Children.Single();
        var obj = draft.AddTable(col, paid);
        draft.Duplicate(obj);
        obj.Pin = paid.Version;
        Assert.Empty(draft.Problems());

        var t = draft.Build();
        Assert.Equal(2, t.Objects().Count());
        Assert.True(f.Svc.PublishTemplate(t).Ok);
    }

    [Fact]
    public void Operations_keep_folders_free_of_objects()
    {
        var draft = TemplateDraft.New("T");
        Assert.Throws<InvalidOperationException>(() => draft.Add(draft.Root, DraftNodeKind.Object, "x"));

        var col = draft.Root.Children.Single();
        var obj = draft.Add(col, DraftNodeKind.Object, "x");
        var folder = draft.Add(draft.Root, DraftNodeKind.Folder, "F");
        var col2 = draft.Add(folder, DraftNodeKind.Collection, "C2");

        Assert.Equal(new[] { col2 }, draft.MoveTargets(obj));              // only collections, not its own
        Assert.DoesNotContain(folder, draft.MoveTargets(folder));          // never into itself
        draft.MoveTo(obj, col2);
        Assert.Contains(obj, col2.Children);
        Assert.Empty(col.Children);
        Assert.Throws<InvalidOperationException>(() => draft.MoveTo(folder, draft.Root));   // already there
    }

    [Fact]
    public void Shift_reorders_siblings_within_bounds()
    {
        var draft = TemplateDraft.New("T");
        var a = draft.Root.Children.Single();
        var b = draft.Add(draft.Root, DraftNodeKind.Collection, "B");
        draft.Shift(b, -1);
        Assert.Equal(new[] { b, a }, draft.Root.Children);
        draft.Shift(b, -1);
        Assert.Equal(new[] { b, a }, draft.Root.Children);
    }

    [Fact]
    public void Changing_the_table_renames_a_default_label_but_keeps_a_custom_one()
    {
        var f = new Fixture();
        var paid = f.Svc.Library.FindLatest(DemoSeed.Paid)!;
        var inc = f.Svc.Library.FindLatest(DemoSeed.Incurred)!;
        var draft = TemplateDraft.New("T");
        var col = draft.Root.Children.Single();

        var a = draft.AddTable(col, paid);
        draft.SetTable(a, inc, paid.Name);
        Assert.Equal(inc.Name, a.Name);

        var b = draft.AddTable(col, paid);
        b.Name = "Custom";
        b.Pin = paid.Version;
        draft.SetTable(b, inc, paid.Name);
        Assert.Equal("Custom", b.Name);
        Assert.Null(b.Pin);
    }
}
