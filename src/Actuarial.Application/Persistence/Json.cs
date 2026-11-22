using System.Text.Json;
using System.Text.Json.Serialization;
using Actuarial.Application.Data;
using Actuarial.Application.UseCases;
using Actuarial.Domain;

namespace Actuarial.Application.Persistence;

/// <summary>JSON conventions: ids as plain strings, versions as plain numbers, enums by name.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Pretty = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new GuidIdConverter<TableId>(g => new(g), x => x.Value));
        o.Converters.Add(new GuidIdConverter<SegmentId>(g => new(g), x => x.Value));
        o.Converters.Add(new GuidIdConverter<AnalysisId>(g => new(g), x => x.Value));
        o.Converters.Add(new GuidIdConverter<TemplateId>(g => new(g), x => x.Value));
        o.Converters.Add(new IntIdConverter<TableVersion>(i => new(i), x => x.Value));
        o.Converters.Add(new IntIdConverter<TemplateVersion>(i => new(i), x => x.Value));
        o.Converters.Add(new IntIdConverter<PeriodLength>(i => new(i), x => x.Months));
        return o;
    }

    public static string Serialize<T>(T value, bool pretty = false) => JsonSerializer.Serialize(value, pretty ? Pretty : Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
                                                   ?? throw new JsonException("The document is empty.");

    private sealed class GuidIdConverter<T>(Func<Guid, T> make, Func<T, Guid> get) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => make(Guid.Parse(r.GetString()!));
        public override void Write(Utf8JsonWriter w, T v, JsonSerializerOptions o) => w.WriteStringValue(get(v).ToString());
        public override T ReadAsPropertyName(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => make(Guid.Parse(r.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter w, T v, JsonSerializerOptions o) => w.WritePropertyName(get(v).ToString());
    }

    private sealed class IntIdConverter<T>(Func<int, T> make, Func<T, int> get) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => make(r.GetInt32());
        public override void Write(Utf8JsonWriter w, T v, JsonSerializerOptions o) => w.WriteNumberValue(get(v));
    }
}

public sealed record StateRow(StateKey Key, StateEntry Entry);
public sealed record DormantRow(StateKey Key, DormantEntry Entry);

public sealed record AnalysisDocument(
    AnalysisId Id, int Revision, SegmentId Segment, DateOnly ValuationDate, TriangleStructure Structure,
    TemplateId Template, TemplateVersion TemplateVersion, ResolvedManifest Manifest, DataSnapshot Data,
    string EngineVersion, Lineage? DerivedFrom, IReadOnlyList<StateRow> State, IReadOnlyList<DormantRow> Dormant,
    SignOff? SignOff, DateTimeOffset CreatedAt, string CreatedBy)
{
    public static AnalysisDocument From(AnalysisRecord r) => new(
        r.Analysis.Id, r.Analysis.Revision, r.Analysis.Segment, r.Analysis.ValuationDate, r.Analysis.Structure,
        r.Analysis.Template, r.Analysis.TemplateVersion, r.Analysis.Manifest, r.Analysis.Data, r.Analysis.EngineVersion,
        r.Analysis.DerivedFrom,
        r.Analysis.State.Select(kv => new StateRow(kv.Key, kv.Value)).ToList(),
        r.Analysis.Dormant.Select(kv => new DormantRow(kv.Key, kv.Value)).ToList(),
        r.Analysis.SignOff, r.CreatedAt, r.CreatedBy);

    public AnalysisRecord ToRecord() => new(
        Analysis.Restore(Id, Revision, Segment, ValuationDate, Structure, Template, TemplateVersion, Manifest, Data,
            EngineVersion, DerivedFrom,
            State.Select(s => KeyValuePair.Create(s.Key, s.Entry)),
            Dormant.Select(d => KeyValuePair.Create(d.Key, d.Entry)),
            SignOff),
        CreatedAt, CreatedBy);
}

public sealed record WorkspaceDocument(
    int SchemaVersion,
    string User,
    IReadOnlyList<TableDefinition> Tables,
    IReadOnlyList<AnalysisTemplate> Templates,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<SnapshotData> Snapshots,
    IReadOnlyList<AnalysisDocument> Analyses,
    IReadOnlyList<AuditEvent> Audit)
{
    public const int CurrentSchema = 1;

    public static WorkspaceDocument From(Workspace w) => new(
        CurrentSchema, w.User,
        w.Library.All.OrderBy(d => d.Name).ThenBy(d => d.Version.Value).ToList(),
        w.Templates.All.OrderBy(t => t.Name).ThenBy(t => t.Version.Value).ToList(),
        w.Segments.ToList(),
        w.Snapshots.Values.ToList(),
        w.Analyses.Select(AnalysisDocument.From).ToList(),
        w.Audit.ToList());

    public Workspace ToWorkspace(ClaimWarehouse warehouse)
    {
        if (SchemaVersion != CurrentSchema) throw new JsonException($"Unsupported workspace schema {SchemaVersion}.");
        var w = new Workspace(warehouse)
        {
            User = User,
            Library = Library.Restore(Tables),
            Templates = TemplateLibrary.Restore(Templates),
        };
        w.Segments.AddRange(Segments);
        foreach (var s in Snapshots) w.Snapshots[s.Snapshot.Id] = s;
        w.Analyses.AddRange(Analyses.Select(a => a.ToRecord()));
        w.Audit.AddRange(Audit);
        return w;
    }
}
