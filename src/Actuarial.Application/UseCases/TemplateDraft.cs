using Actuarial.Domain;

namespace Actuarial.Application.UseCases;

public enum DraftNodeKind { Folder, Collection, Object }

/// <summary>One node of a template being edited. Node ids are kept from the source template.</summary>
public sealed class DraftNode
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DraftNodeKind Kind { get; init; }
    public string Name { get; set; } = "";
    /// <summary>Objects only: the library table shown.</summary>
    public TableId? Table { get; set; }
    /// <summary>Objects only: a pinned version, or null to follow the latest.</summary>
    public TableVersion? Pin { get; set; }
    public List<DraftNode> Children { get; } = new();

    public bool IsContainer => Kind != DraftNodeKind.Object;

    /// <summary>The kinds of node this node can hold: folders hold folders or collections, collections hold objects.</summary>
    public bool CanHold(DraftNodeKind child) => Kind switch
    {
        DraftNodeKind.Folder     => child is DraftNodeKind.Folder or DraftNodeKind.Collection,
        DraftNodeKind.Collection => child is DraftNodeKind.Object,
        _                        => false
    };
}

/// <summary>
/// Editing state for a template, for the form-based editor. Published templates are immutable records; this mutable
/// working copy is turned back into one by <see cref="Build"/>, and publish (<see cref="TemplateLibrary.Publish"/>) stays
/// the only gate. The operations here keep the tree well formed (a folder never holds an object) so the user cannot
/// build a shape the domain would refuse.
/// </summary>
public sealed class TemplateDraft
{
    public TemplateId Id { get; }
    public TemplateVersion Version { get; }
    public string Name { get; set; }
    public DraftNode Root { get; }

    private TemplateDraft(TemplateId id, TemplateVersion version, string name, DraftNode root)
    {
        Id = id; Version = version; Name = name; Root = root;
    }

    public static TemplateDraft From(AnalysisTemplate t) => new(t.Id, t.Version, t.Name, Convert(t.Root));

    public static TemplateDraft New(string name)
    {
        var d = new TemplateDraft(new TemplateId(Guid.NewGuid()), new TemplateVersion(1), name,
            new DraftNode { Kind = DraftNodeKind.Folder, Name = name });
        d.Add(d.Root, DraftNodeKind.Collection, "New collection");
        return d;
    }

    private static DraftNode Convert(TemplateNode n)
    {
        switch (n)
        {
            case FolderNode f:
                var folder = new DraftNode { Id = f.NodeId, Kind = DraftNodeKind.Folder, Name = f.Name };
                folder.Children.AddRange(f.Children.Select(Convert));
                return folder;
            case CollectionNode c:
                var col = new DraftNode { Id = c.NodeId, Kind = DraftNodeKind.Collection, Name = c.Name };
                col.Children.AddRange(c.Objects.Select(Convert));
                return col;
            case ObjectNode o:
                return new DraftNode { Id = o.NodeId, Kind = DraftNodeKind.Object, Name = o.Name, Table = o.Table.Table, Pin = o.Table.Version };
            default:
                throw new ArgumentException($"Unknown template node {n.GetType().Name}.");
        }
    }

    /// <summary>The immutable template. The root folder carries the template's name.</summary>
    public AnalysisTemplate Build()
    {
        TemplateNode B(DraftNode n) => n.Kind switch
        {
            DraftNodeKind.Folder     => new FolderNode(n.Id, n.Name.Trim(), n.Children.Select(B).ToList()),
            DraftNodeKind.Collection => new CollectionNode(n.Id, n.Name.Trim(), n.Children.Select(c => (ObjectNode)B(c)).ToList()),
            _                        => new ObjectNode(n.Id, n.Name.Trim(), new TableRef(n.Table!.Value, n.Pin)),
        };
        var root = new FolderNode(Root.Id, Name.Trim(), Root.Children.Select(B).ToList());
        return new AnalysisTemplate(Id, Version, Name.Trim(), root);
    }

    /// <summary>Problems the editor can point at before publish (publish re-checks everything).</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) p.Add("The template needs a name.");
        foreach (var n in All().Where(n => n != Root))
        {
            if (string.IsNullOrWhiteSpace(n.Name)) p.Add($"A {n.Kind.ToString().ToLowerInvariant()} has no name.");
            if (n.Kind == DraftNodeKind.Object && n.Table is null) p.Add($"Object '{n.Name}' has no table.");
        }
        if (!All().Any(n => n.Kind == DraftNodeKind.Object)) p.Add("Add at least one table.");
        return p;
    }

    public IEnumerable<DraftNode> All()
    {
        IEnumerable<DraftNode> W(DraftNode n) => new[] { n }.Concat(n.Children.SelectMany(W));
        return W(Root);
    }

    public DraftNode? ParentOf(DraftNode node) => All().FirstOrDefault(p => p.Children.Contains(node));

    // ───────────── Operations ─────────────

    public DraftNode Add(DraftNode parent, DraftNodeKind kind, string name)
    {
        if (!parent.CanHold(kind))
            throw new InvalidOperationException($"A {parent.Kind.ToString().ToLowerInvariant()} cannot hold a {kind.ToString().ToLowerInvariant()}.");
        var n = new DraftNode { Kind = kind, Name = name };
        parent.Children.Add(n);
        return n;
    }

    public DraftNode AddTable(DraftNode collection, TableDefinition table)
    {
        var n = Add(collection, DraftNodeKind.Object, table.Name);
        n.Table = table.Id;
        return n;
    }

    /// <summary>Points an object at another table. A name that still showed the old table's name follows the new one.</summary>
    public void SetTable(DraftNode obj, TableDefinition table, string? oldTableName)
    {
        if (obj.Kind != DraftNodeKind.Object) throw new InvalidOperationException("Only objects show a table.");
        if (string.IsNullOrWhiteSpace(obj.Name) || obj.Name == oldTableName) obj.Name = table.Name;
        obj.Table = table.Id;
        obj.Pin = null;
    }

    public void Remove(DraftNode node)
    {
        if (node == Root) throw new InvalidOperationException("The root folder cannot be removed.");
        ParentOf(node)?.Children.Remove(node);
    }

    /// <summary>Moves a node one step among its siblings (-1 up, +1 down).</summary>
    public void Shift(DraftNode node, int delta)
    {
        var parent = ParentOf(node);
        if (parent is null) return;
        var i = parent.Children.IndexOf(node);
        var j = Math.Clamp(i + delta, 0, parent.Children.Count - 1);
        if (i == j) return;
        parent.Children.RemoveAt(i);
        parent.Children.Insert(j, node);
    }

    /// <summary>Containers <paramref name="node"/> could move into: the right kind, not itself or inside itself, not its current parent.</summary>
    public IEnumerable<DraftNode> MoveTargets(DraftNode node)
    {
        var inside = new HashSet<DraftNode>();
        void Mark(DraftNode n) { inside.Add(n); n.Children.ForEach(Mark); }
        Mark(node);
        var parent = ParentOf(node);
        return All().Where(t => t.CanHold(node.Kind) && !inside.Contains(t) && t != parent);
    }

    public void MoveTo(DraftNode node, DraftNode target)
    {
        if (!MoveTargets(node).Contains(target)) throw new InvalidOperationException("The node cannot move there.");
        ParentOf(node)!.Children.Remove(node);
        target.Children.Add(node);
    }

    public DraftNode Duplicate(DraftNode node)
    {
        var parent = ParentOf(node) ?? throw new InvalidOperationException("The root folder cannot be duplicated.");
        DraftNode Copy(DraftNode n)
        {
            var c = new DraftNode { Kind = n.Kind, Name = n.Name, Table = n.Table, Pin = n.Pin };
            c.Children.AddRange(n.Children.Select(Copy));
            return c;
        }
        var copy = Copy(node);
        parent.Children.Insert(parent.Children.IndexOf(node) + 1, copy);
        return copy;
    }

    /// <summary>A breadcrumb for a container, used to label move targets.</summary>
    public string PathOf(DraftNode node)
    {
        var parts = new List<string>();
        for (var n = node; n is not null && n != Root; n = ParentOf(n)) parts.Insert(0, n.Name);
        return parts.Count == 0 ? $"{Name} (top level)" : string.Join(" › ", parts);
    }
}
