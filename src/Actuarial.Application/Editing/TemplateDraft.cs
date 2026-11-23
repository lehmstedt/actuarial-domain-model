using Actuarial.Domain;

namespace Actuarial.Application.Editing;

public enum TemplateNodeKind { Folder, Collection, Object }

/// <summary>Editable template tree node. Folders hold folders and collections; collections hold objects.</summary>
public sealed class TemplateNodeDraft
{
    public Guid NodeId { get; init; } = Guid.NewGuid();
    public TemplateNodeKind Kind { get; init; }
    public string Name { get; set; } = "";
    public TableId? Table { get; set; }
    public int? PinnedVersion { get; set; }
    public List<TemplateNodeDraft> Children { get; } = new();

    public static TemplateNodeDraft Folder(string name) => new() { Kind = TemplateNodeKind.Folder, Name = name };
    public static TemplateNodeDraft Collection(string name) => new() { Kind = TemplateNodeKind.Collection, Name = name };
    public static TemplateNodeDraft Object(TableDefinition t) => new() { Kind = TemplateNodeKind.Object, Name = t.Name, Table = t.Id };

    public static TemplateNodeDraft From(TemplateNode n)
    {
        var d = n switch
        {
            FolderNode => new TemplateNodeDraft { NodeId = n.NodeId, Kind = TemplateNodeKind.Folder, Name = n.Name },
            CollectionNode => new TemplateNodeDraft { NodeId = n.NodeId, Kind = TemplateNodeKind.Collection, Name = n.Name },
            ObjectNode o => new TemplateNodeDraft { NodeId = n.NodeId, Kind = TemplateNodeKind.Object, Name = n.Name, Table = o.Table.Table, PinnedVersion = o.Table.Version?.Value },
            _ => throw new ArgumentOutOfRangeException(nameof(n))
        };
        var children = n switch { FolderNode f => f.Children, CollectionNode c => c.Objects.Cast<TemplateNode>(), _ => Enumerable.Empty<TemplateNode>() };
        d.Children.AddRange(children.Select(From));
        return d;
    }

    /// <summary>The node that directly contains <paramref name="child"/>, searching from here.</summary>
    public TemplateNodeDraft? ParentOf(TemplateNodeDraft child)
    {
        if (Children.Contains(child)) return this;
        foreach (var c in Children)
            if (c.ParentOf(child) is { } p) return p;
        return null;
    }

    public int CountObjects() => Kind == TemplateNodeKind.Object ? 1 : Children.Sum(c => c.CountObjects());

    /// <summary>Folders hold folders or collections; collections hold objects; objects hold nothing.</summary>
    public bool CanHold(TemplateNodeKind child) => Kind switch
    {
        TemplateNodeKind.Folder     => child is TemplateNodeKind.Folder or TemplateNodeKind.Collection,
        TemplateNodeKind.Collection => child is TemplateNodeKind.Object,
        _                           => false
    };

    /// <summary>A deep copy with fresh node ids.</summary>
    public TemplateNodeDraft Copy()
    {
        var c = new TemplateNodeDraft { Kind = Kind, Name = Name, Table = Table, PinnedVersion = PinnedVersion };
        c.Children.AddRange(Children.Select(x => x.Copy()));
        return c;
    }

    internal TemplateNode Build(List<string> problems)
    {
        var name = Name.Trim();
        if (name.Length == 0) problems.Add($"A {Kind.ToString().ToLowerInvariant()} needs a name.");
        switch (Kind)
        {
            case TemplateNodeKind.Object:
                if (Table is not { } t) { problems.Add($"Object '{name}': choose a table."); return new ObjectNode(NodeId, name, default); }
                return new ObjectNode(NodeId, name, new TableRef(t, PinnedVersion is { } v ? new TableVersion(v) : null));
            case TemplateNodeKind.Collection:
                return new CollectionNode(NodeId, name, Children.Select(c => c.Build(problems)).OfType<ObjectNode>().ToList());
            default:
                return new FolderNode(NodeId, name, Children.Select(c => c.Build(problems)).ToList());
        }
    }
}

public sealed class TemplateDraft
{
    public TemplateId Id { get; init; }
    public int Version { get; init; }
    public bool IsNew => Version == 1;
    public string Name { get; set; } = "";
    public TemplateNodeDraft Root { get; init; } = TemplateNodeDraft.Folder("");

    public static TemplateDraft Edit(AnalysisTemplate latest, TemplateVersion next) =>
        new() { Id = latest.Id, Version = next.Value, Name = latest.Name, Root = TemplateNodeDraft.From(latest.Root) };

    public static TemplateDraft New() => new()
    {
        Id = new TemplateId(Guid.NewGuid()), Version = 1, Name = "New template",
        Root = new TemplateNodeDraft { Kind = TemplateNodeKind.Folder, Children = { TemplateNodeDraft.Collection("Tables") } }
    };

    public IEnumerable<TemplateNodeDraft> All()
    {
        IEnumerable<TemplateNodeDraft> W(TemplateNodeDraft n) => n.Children.SelectMany(W).Prepend(n);
        return W(Root);
    }

    public TemplateNodeDraft? ParentOf(TemplateNodeDraft node) => node == Root ? null : Root.ParentOf(node);

    /// <summary>Containers <paramref name="node"/> can move into: the right kind, not itself or anything inside it, not its current parent.</summary>
    public IEnumerable<TemplateNodeDraft> MoveTargets(TemplateNodeDraft node)
    {
        var parent = ParentOf(node);
        return All().Where(t => t.CanHold(node.Kind) && t != parent && t != node && node.ParentOf(t) is null);
    }

    public void MoveTo(TemplateNodeDraft node, TemplateNodeDraft target)
    {
        if (!MoveTargets(node).Contains(target)) throw new InvalidOperationException("The node cannot move there.");
        ParentOf(node)!.Children.Remove(node);
        target.Children.Add(node);
    }

    /// <summary>Copies a node (and what it holds) right after itself.</summary>
    public TemplateNodeDraft Duplicate(TemplateNodeDraft node)
    {
        var parent = ParentOf(node) ?? throw new InvalidOperationException("The root folder cannot be duplicated.");
        var copy = node.Copy();
        parent.Children.Insert(parent.Children.IndexOf(node) + 1, copy);
        return copy;
    }

    /// <summary>Breadcrumb for a container, used to label move targets.</summary>
    public string PathOf(TemplateNodeDraft node)
    {
        var parts = new List<string>();
        for (var n = node; n is not null && n != Root; n = ParentOf(n)) parts.Insert(0, n.Name);
        return parts.Count == 0 ? "Top level" : string.Join(" › ", parts);
    }

    /// <summary>The root folder carries the template's name.</summary>
    public (AnalysisTemplate? Template, IReadOnlyList<string> Problems) Build()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("Give the template a name.");
        var children = Root.Children.Select(c => c.Build(problems)).ToList();
        var root = new FolderNode(Root.NodeId, Name.Trim(), children);
        var t = new AnalysisTemplate(Id, new TemplateVersion(Version), Name.Trim(), root);
        if (problems.Count == 0) problems.AddRange(t.SelfCheck());
        return (problems.Count == 0 ? t : null, problems.Distinct().ToList());
    }
}
