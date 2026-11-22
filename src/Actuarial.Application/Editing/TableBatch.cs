using Actuarial.Domain;

namespace Actuarial.Application.Editing;

/// <summary>A table that a reference can point at: a library table, or a draft in the same batch.</summary>
public sealed record SourceTable(TableId Id, string Name, TableKind Kind, IReadOnlyList<int> Versions, bool InBatch);

/// <summary>
/// The tables being edited together. They publish atomically, so a breaking change can go out with its dependents,
/// and drafts may reference each other (floating references resolve to the draft, which becomes the latest version).
/// </summary>
public sealed class TableBatch(Library library)
{
    public List<TableDraft> Drafts { get; } = new();

    private readonly HashSet<TableId> _building = new();

    public TableDraft? DraftOf(TableId id) => Drafts.FirstOrDefault(d => d.Id == id);

    /// <summary>Resolves as publish will: a floating reference to a table in the batch means the draft.</summary>
    public TableDefinition? Resolve(TableRef r)
    {
        if (r.IsFloating && DraftOf(r.Table) is { } d && _building.Add(d.Id))
        {
            try { if (d.Build(Resolve).Definition is { } built) return built; }
            finally { _building.Remove(d.Id); }
        }
        return r.Version is { } v ? library.Find(r.Table, v) : library.FindLatest(r.Table);
    }

    /// <summary>Columns a reference can read, given its table and version choice.</summary>
    public IReadOnlyList<ColumnSchema> ColumnsOf(TableId table, int? pinned) =>
        Resolve(new TableRef(table, pinned is { } v ? new TableVersion(v) : null))?.Output.Columns ?? Array.Empty<ColumnSchema>();

    /// <summary>Everything <paramref name="self"/> may reference: library tables and the other drafts.</summary>
    public IReadOnlyList<SourceTable> Sources(TableDraft self)
    {
        var list = library.TableIds.Where(id => id != self.Id)
            .Select(id => library.FindLatest(id)!)
            .Select(t => new SourceTable(t.Id, DraftOf(t.Id)?.Name is { Length: > 0 } n ? n : t.Name, TableKinds.Of(t),
                library.Versions(t.Id).Select(x => x.Version.Value).ToList(), DraftOf(t.Id) is not null))
            .ToList();
        list.AddRange(Drafts.Where(d => d.IsNew && d.Id != self.Id)
            .Select(d => new SourceTable(d.Id, d.Name, d.Kind, Array.Empty<int>(), true)));
        return list.OrderBy(s => s.Kind).ThenBy(s => s.Name).ToList();
    }

    public string NameOf(TableId id) => DraftOf(id)?.Name ?? library.FindLatest(id)?.Name ?? "(unknown table)";

    /// <summary>Published tables that read one of the drafts and are not in the batch yet: candidates to publish alongside.</summary>
    public IEnumerable<TableDefinition> Dependents() =>
        Drafts.SelectMany(d => library.Dependents(d.Id)).DistinctBy(t => t.Id).Where(t => DraftOf(t.Id) is null).OrderBy(t => t.Name);

    /// <summary>All definitions, or every form problem labelled with its table.</summary>
    public (IReadOnlyList<TableDefinition>? Definitions, IReadOnlyList<string> Problems) Build()
    {
        var defs = new List<TableDefinition>();
        var problems = new List<string>();
        foreach (var d in Drafts)
        {
            var (def, p) = d.Build(Resolve);
            if (def is not null) defs.Add(def);
            problems.AddRange(p.Select(x => Drafts.Count > 1 ? $"{(d.Name.Length > 0 ? d.Name : "Untitled")}: {x}" : x));
        }
        return (problems.Count == 0 ? defs : null, problems);
    }
}
