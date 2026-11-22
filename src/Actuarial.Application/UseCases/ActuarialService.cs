using Actuarial.Application.Calculation;
using Actuarial.Application.Data;
using Actuarial.Application.Persistence;
using Actuarial.Domain;

namespace Actuarial.Application.UseCases;

/// <summary>A use case refused the request. The message is meant for the user.</summary>
public sealed class UseCaseException(string message) : Exception(message);

public sealed record UpgradeCandidate(TableId Table, string Name, TableVersion Pinned, TableVersion Latest);

public sealed record VerificationResult(bool Matches, string StoredHash, string RecomputedHash);

/// <summary>An analysis as the user sees it: definitions, state, results and what is still missing.</summary>
public sealed record AnalysisView(
    AnalysisRecord Record,
    Segment Segment,
    AnalysisTemplate Template,
    ResultSnapshot Results,
    bool ResultsAreMaterialised,
    IReadOnlyList<StateKey> NeedsInput,
    IReadOnlyList<ExposureKey> Exposures,
    IReadOnlyList<DevKey> Developments,
    IReadOnlyList<int> LatestDevIndex)
{
    public Analysis Analysis => Record.Analysis;
}

/// <summary>
/// Application use cases over the domain model. Every mutation of the workspace goes through here,
/// is validated by the domain, and leaves an audit event.
/// </summary>
public sealed class ActuarialService
{
    private readonly Func<DateTimeOffset> _clock;
    public Workspace Workspace { get; private set; }
    public CalculationEngine Engine { get; }

    /// <summary>Raised after every successful mutation (the web app persists on it).</summary>
    public event Action? Changed;

    public ActuarialService(Workspace workspace, Func<DateTimeOffset>? clock = null)
    {
        Workspace = workspace;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Engine = new CalculationEngine(SnapshotDataFor);
    }

    public Library Library => Workspace.Library;
    public TemplateLibrary Templates => Workspace.Templates;
    private DateTimeOffset Now => _clock();

    public void Replace(Workspace w) { Workspace = w; Notify(); }

    private void Notify() => Changed?.Invoke();

    private void Log(AuditKind kind, string summary, Analysis? a = null, MigrationReport? report = null, IReadOnlyList<string>? details = null) =>
        Workspace.Audit.Add(new AuditEvent(Guid.NewGuid(), Now, Workspace.User, kind, summary, a?.Id, a?.Revision, report, details));

    // ───────────────────────── Library ─────────────────────────

    public string TableName(TableId id) => Library.FindLatest(id)?.Name ?? id.ToString();

    /// <summary>The latest version with the next version number: the starting point for an edit.</summary>
    public TableDefinition DraftNextVersion(TableId id)
    {
        var latest = Library.FindLatest(id) ?? throw new UseCaseException("Unknown table.");
        return latest with { Version = Library.NextVersion(id) };
    }

    public PublishResult PublishTables(IReadOnlyCollection<TableDefinition> batch)
    {
        var result = Library.Publish(batch);
        var names = string.Join(", ", batch.Select(d => $"{d.Name} v{d.Version.Value}"));
        if (result.Ok) Log(AuditKind.TablesPublished, $"Published {names}");
        else Log(AuditKind.PublishRejected, $"Rejected {names}", details: result.Errors.Select(e => e.Message).ToList());
        Notify();
        return result;
    }

    public static IReadOnlyList<TableDefinition> ParseTables(string json)
    {
        json = json.Trim();
        try
        {
            return json.StartsWith('[')
                ? Json.Deserialize<List<TableDefinition>>(json)
                : new[] { Json.Deserialize<TableDefinition>(json) };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or FormatException or ArgumentException)
        {
            throw new UseCaseException($"The JSON could not be read: {e.Message}");
        }
    }

    // ───────────────────────── Templates ─────────────────────────

    public AnalysisTemplate DraftNextTemplateVersion(TemplateId id)
    {
        var latest = Templates.FindLatest(id) ?? throw new UseCaseException("Unknown template.");
        return latest with { Version = Templates.NextVersion(id) };
    }

    public TemplatePublishResult PublishTemplate(AnalysisTemplate template)
    {
        var r = Templates.Publish(template, Library);
        if (r.Ok) { Log(AuditKind.TemplatePublished, $"Published template {template.Name} v{template.Version.Value}"); Notify(); }
        return r;
    }

    public static AnalysisTemplate ParseTemplate(string json)
    {
        try { return Json.Deserialize<AnalysisTemplate>(json); }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or FormatException or ArgumentException)
        {
            throw new UseCaseException($"The JSON could not be read: {e.Message}");
        }
    }

    // ───────────────────────── Segments and data ─────────────────────────

    public Segment CreateSegment(string name, IReadOnlyDictionary<string, string> filters)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new UseCaseException("A segment needs a name.");
        if (Workspace.Segments.Any(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new UseCaseException($"A segment called '{name}' already exists.");
        var s = new Segment(new SegmentId(Guid.NewGuid()), name.Trim(), filters);
        if (!Workspace.Warehouse.Claims.Any(c => ClaimWarehouse.Matches(s, c.Lob, c.Region)))
            throw new UseCaseException("These filters select no claim data.");
        Workspace.Segments.Add(s);
        Log(AuditKind.SegmentCreated, $"Created segment {s.Name} ({FilterText(s)})");
        Notify();
        return s;
    }

    public static string FilterText(Segment s) =>
        s.Filters.Count == 0 ? "all data" : string.Join(", ", s.Filters.Select(f => $"{f.Key} = {f.Value}"));

    public Segment SegmentOf(SegmentId id) => Workspace.Segments.First(s => s.Id == id);

    /// <summary>Freezes the segment's data at a valuation date. An identical existing snapshot is reused.</summary>
    public DataSnapshot TakeSnapshot(SegmentId segmentId, DateOnly valuation)
    {
        var segment = SegmentOf(segmentId);
        if (!Months.IsMonthEnd(valuation) || valuation.Month % Workspace.Warehouse.GranularityMonths != 0)
            throw new UseCaseException("The claim data is quarterly: the valuation date must be a quarter end.");
        if (valuation > Workspace.Warehouse.DataThrough)
            throw new UseCaseException($"Claim data only runs to {Workspace.Warehouse.DataThrough:yyyy-MM-dd}.");

        var (claims, premium) = Workspace.Warehouse.Extract(segment, valuation);
        var hash = SnapshotData.Hash(claims, premium);
        var existing = Workspace.Snapshots.Values.FirstOrDefault(s =>
            s.Snapshot.Segment == segmentId && s.Snapshot.ValuationDate == valuation && s.Snapshot.ContentHash == hash);
        if (existing is not null) return existing.Snapshot;

        var snap = new DataSnapshot(Guid.NewGuid(), segmentId, valuation, hash);
        Workspace.Snapshots[snap.Id] = new SnapshotData(snap, Now, claims, premium);
        Log(AuditKind.SnapshotTaken, $"Snapshot of {segment.Name} at {valuation:yyyy-MM-dd} ({claims.Count} cells, hash {hash[..12]})");
        Notify();
        return snap;
    }

    public SnapshotData SnapshotDataFor(DataSnapshot s) =>
        Workspace.Snapshots.TryGetValue(s.Id, out var d) ? d : throw new UseCaseException("The data snapshot is missing from the workspace.");

    // ───────────────────────── Analyses: reading ─────────────────────────

    public AnalysisRecord Get(AnalysisId id, int revision) =>
        Workspace.Analyses.FirstOrDefault(r => r.Analysis.Id == id && r.Analysis.Revision == revision)
        ?? throw new UseCaseException("Analysis not found.");

    public AnalysisRecord LatestRevision(AnalysisId id) =>
        Workspace.Analyses.Where(r => r.Analysis.Id == id).MaxBy(r => r.Analysis.Revision)
        ?? throw new UseCaseException("Analysis not found.");

    public IEnumerable<AnalysisRecord> Revisions(AnalysisId id) =>
        Workspace.Analyses.Where(r => r.Analysis.Id == id).OrderBy(r => r.Analysis.Revision);

    /// <summary>Drafts compute on read; signed-off analyses show their materialised results.</summary>
    public AnalysisView Open(AnalysisId id, int revision)
    {
        var rec = Get(id, revision);
        var a = rec.Analysis;
        var materialised = a.SignOff is not null;
        var results = a.SignOff?.Results ?? Engine.Compute(a, Library);
        return new AnalysisView(rec, SegmentOf(a.Segment), Templates.Get(a.Template, a.TemplateVersion), results, materialised,
            StateValidator.Missing(a.State, a.Structure, a.Manifest, Library),
            a.Structure.ExposureKeys().ToList(), a.Structure.DevelopmentKeys().ToList(),
            a.Structure.ExposureKeys().Select(e => a.Structure.LatestDevIndex(e, a.ValuationDate)).ToList());
    }

    public IReadOnlyList<UpgradeCandidate> UpgradesAvailable(Analysis a) =>
        a.Manifest.Pinned
            .Select(p => (p.Key, Pinned: p.Value, Latest: Library.Latest(p.Key)!.Value))
            .Where(x => x.Latest.Value > x.Pinned.Value)
            .Select(x => new UpgradeCandidate(x.Key, Library.Get(x.Key, x.Latest).Name, x.Pinned, x.Latest))
            .ToList();

    public bool IsLatestRevision(Analysis a) => LatestRevision(a.Id).Analysis.Revision == a.Revision;

    // ───────────────────────── Analyses: creating ─────────────────────────

    public AnalysisRecord CreateAnalysis(SegmentId segment, DateOnly valuation, TriangleStructure structure, TemplateId templateId)
    {
        RequireStructure(structure, valuation);
        var template = Templates.FindLatest(templateId) ?? throw new UseCaseException("Unknown template.");
        var manifest = ManifestFor(template, null);
        var snap = TakeSnapshot(segment, valuation);

        var a = new Analysis(AnalysisId.New(), 1, segment, valuation, structure, template.Id, template.Version, manifest, snap, Engine.Version);
        var rec = Add(a);
        Log(AuditKind.AnalysisCreated, $"Created analysis of {SegmentOf(segment).Name} at {valuation:yyyy-MM-dd} with {template.Name} v{template.Version.Value}", a);
        Notify();
        return rec;
    }

    private AnalysisRecord Add(Analysis a)
    {
        var rec = new AnalysisRecord(a, Now, Workspace.User);
        Workspace.Analyses.Add(rec);
        return rec;
    }

    private ResolvedManifest ManifestFor(AnalysisTemplate template, ResolvedManifest? preferred)
    {
        try { return ManifestBuilder.Build(template, Library, preferred); }
        catch (InvalidOperationException e) { throw new UseCaseException(e.Message); }
    }

    private void RequireStructure(TriangleStructure s, DateOnly valuation)
    {
        var problems = s.Validate(valuation).ToList();
        var g = Workspace.Warehouse.GranularityMonths;
        if (s.ExposurePeriodLength.Months % g != 0 || s.DevelopmentPeriodLength.Months % g != 0)
            problems.Add($"The claim data is quarterly: period lengths must be multiples of {g} months.");
        if (problems.Count > 0) throw new UseCaseException(string.Join(" ", problems));
    }

    // ───────────────────────── Analyses: user state ─────────────────────────

    /// <summary>Records a selection or an entered value after checking it means something for the pinned definitions.</summary>
    public void Select(Analysis a, StateKey key, StateValue value)
    {
        RequireEditable(a);
        if (StateValidator.Problem(key, value, a.Manifest, Library) is { } p) throw new UseCaseException($"Not accepted: {p}.");
        a.Select(key, value);
        Notify();
    }

    /// <summary>Applies one value to many keys at once (e.g. the same statistic for every age).</summary>
    public int SelectMany(Analysis a, IEnumerable<StateKey> keys, StateValue value)
    {
        RequireEditable(a);
        var n = 0;
        foreach (var k in keys)
            if (StateValidator.Problem(k, value, a.Manifest, Library) is null) { a.Select(k, value); n++; }
        Notify();
        return n;
    }

    public void Clear(Analysis a, StateKey key)
    {
        RequireEditable(a);
        a.Clear(key);
        Notify();
    }

    private void RequireEditable(Analysis a)
    {
        if (a.Status == AnalysisStatus.SignedOff) throw new UseCaseException("This analysis is signed off. Create a new revision to change it.");
        if (!IsLatestRevision(a)) throw new UseCaseException("Only the latest revision of an analysis can be edited.");
    }

    // ───────────────────────── Analyses: operations ─────────────────────────

    public MigrationReport ApplyTemplate(Analysis a, TemplateId templateId)
    {
        RequireEditable(a);
        var template = Templates.FindLatest(templateId) ?? throw new UseCaseException("Unknown template.");
        var r = Run(() => AnalysisOperations.ApplyTemplate(a, template, Library));
        Log(AuditKind.TemplateApplied, $"Applied template {template.Name} v{template.Version.Value}", a, r.Report);
        Notify();
        return r.Report;
    }

    public OperationResult RollForward(Analysis source, DateOnly newValuation, GrowthMode growth)
    {
        if (Workspace.Analyses.Any(r => r.Analysis.DerivedFrom is { Operation: Operation.RollForward } l && l.SourceAnalysis == source.Id
                                        && r.Analysis.ValuationDate == newValuation && r.Analysis.Segment == source.Segment))
            throw new UseCaseException($"This analysis has already been rolled forward to {newValuation:yyyy-MM-dd}.");
        RequireStructure(source.Structure.RollTo(newValuation, growth), newValuation);
        var snap = TakeSnapshot(source.Segment, newValuation);
        var r = Run(() => AnalysisOperations.RollForward(source, newValuation, SnapshotDataFor(snap).Snapshot, growth, AnalysisId.New(), Engine.Version, Library));
        Add(r.Analysis);
        Log(AuditKind.RolledForward, $"Rolled forward to {newValuation:yyyy-MM-dd} ({growth})", r.Analysis, r.Report);
        Notify();
        return r;
    }

    public MigrationReport Reshape(Analysis a, TriangleStructure to)
    {
        RequireEditable(a);
        RequireStructure(to, a.ValuationDate);
        var r = Run(() => AnalysisOperations.Reshape(a, to, Library));
        Log(AuditKind.Reshaped, $"Reshaped to {to.Describe()}", a, r.Report);
        Notify();
        return r.Report;
    }

    public OperationResult Duplicate(Analysis source, SegmentId target)
    {
        var snap = TakeSnapshot(target, source.ValuationDate);
        var r = Run(() => AnalysisOperations.Duplicate(source, target, snap, AnalysisId.New(), Engine.Version, Library));
        Add(r.Analysis);
        Log(AuditKind.Duplicated, $"Duplicated {SegmentOf(source.Segment).Name} onto {SegmentOf(target).Name}", r.Analysis, r.Report);
        Notify();
        return r;
    }

    /// <summary>New draft revision. With upgrade, every table resolves to its latest version and the template to its latest.</summary>
    public OperationResult NewRevision(Analysis source, bool upgrade)
    {
        if (!IsLatestRevision(source)) throw new UseCaseException("A newer revision already exists.");
        if (source.Status != AnalysisStatus.SignedOff && !upgrade)
            throw new UseCaseException("A draft can be edited directly; a new revision is for signed-off analyses or upgrades.");

        var template = upgrade ? Templates.FindLatest(source.Template)! : Templates.Get(source.Template, source.TemplateVersion);
        var manifest = upgrade ? ManifestFor(template, null) : source.Manifest;
        var r = Run(() => AnalysisOperations.NewRevision(source, template.Version, manifest, Engine.Version, Library));
        Add(r.Analysis);
        var changes = upgrade
            ? manifest.Pinned.Where(p => !source.Manifest.Pinned.TryGetValue(p.Key, out var v) || v != p.Value)
                .Select(p => $"{Library.Get(p.Key, p.Value).Name}: {(source.Manifest.Pinned.TryGetValue(p.Key, out var o) ? $"v{o.Value}" : "new")} → v{p.Value.Value}").ToList()
            : new List<string>();
        Log(AuditKind.NewRevision, upgrade ? $"Revision {r.Analysis.Revision} upgraded to the latest definitions" : $"Revision {r.Analysis.Revision} opened for changes",
            r.Analysis, r.Report, changes);
        Notify();
        return r;
    }

    /// <summary>Computes, hashes and freezes the results with the analysis.</summary>
    public ResultSnapshot SignOff(Analysis a, string user)
    {
        RequireEditable(a);
        var results = Engine.Compute(a, Library);
        Run(() => { a.Sign(user, results, Now); return 0; });
        var open = StateValidator.Missing(a.State, a.Structure, a.Manifest, Library).Count;
        Log(AuditKind.SignedOff, $"Signed off by {user} (hash {results.Hash[..12]}){(open > 0 ? $", {open} input(s) left open" : "")}", a);
        Notify();
        return results;
    }

    /// <summary>Recomputes a signed-off analysis from its pinned inputs and compares hashes.</summary>
    public VerificationResult Verify(Analysis a)
    {
        if (a.SignOff is null) throw new UseCaseException("Only signed-off analyses can be verified.");
        var stored = a.SignOff.Results.Hash;
        string recomputed;
        if (a.SignOff.Results.EngineVersion != Engine.Version)
            recomputed = $"engine {Engine.Version} cannot reproduce {a.SignOff.Results.EngineVersion}";
        else
        {
            var fresh = Engine.Compute(a, Library);
            recomputed = fresh.Hash;
        }
        var ok = stored == recomputed;
        Log(AuditKind.Verified, ok ? "Verification passed: results reproduce exactly" : "Verification FAILED: results differ", a);
        Notify();
        return new VerificationResult(ok, stored, recomputed);
    }

    private static T Run<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { throw new UseCaseException(e.Message); }
    }

    // ───────────────────────── Describing state ─────────────────────────

    public string Describe(StateKey k, ResolvedManifest m)
    {
        var name = m.Pinned.TryGetValue(k.Table, out var v) && Library.Find(k.Table, v) is { } d ? d.Name : TableName(k.Table);
        var col = k.ColumnKey.StartsWith("weight:", StringComparison.Ordinal) && Guid.TryParse(k.ColumnKey[7..], out var g)
            ? $"weight of {TableName(new TableId(g))}"
            : k.ColumnKey;
        var where = string.Join(" · ", new[] { k.Exposure?.Label, k.Development is { } dv ? $"dev {dv.Label}" : null }.Where(x => x is not null));
        return $"{name} / {col}{(where.Length > 0 ? " / " + where : "")}";
    }

    public string Describe(StateValue v) => v switch
    {
        SelectedStatistic s => $"statistic {s.StatisticKey}",
        SelectedMethod sm => $"method {TableName(sm.MethodTable)}",
        SelectedAverage sa => $"{sa.Average.ToString().ToLowerInvariant()} average",
        ManualValue mv => mv.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString()!
    };
}
