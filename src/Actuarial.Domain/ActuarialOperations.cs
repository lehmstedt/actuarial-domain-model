// Analysis operations: apply template, roll forward, reshape, duplicate (to another segment).
// Every operation is built on one state migrator that remaps state by period identity.

namespace Actuarial.Domain;

public enum Operation { ApplyTemplate, RollForward, Reshape, Duplicate, NewRevision }

public sealed record Lineage(Operation Operation, AnalysisId SourceAnalysis, int SourceRevision);

public sealed record MigrationEntry(StateKey Key, string Detail);

/// <summary>What happened to the state. Carried = same period. Remapped = split or merged. Dormant = kept but inactive.</summary>
public sealed record MigrationReport(
    IReadOnlyList<MigrationEntry> Carried,
    IReadOnlyList<MigrationEntry> Remapped,
    IReadOnlyList<MigrationEntry> Dormant,
    IReadOnlyList<StateKey> NeedsInput);

public sealed record OperationResult(Analysis Analysis, MigrationReport Report);

// ───────────────────────── Operations ─────────────────────────

public static class AnalysisOperations
{
    /// <summary>In place, drafts only. Existing pins are kept; only newly needed tables resolve to latest.</summary>
    public static OperationResult ApplyTemplate(Analysis draft, AnalysisTemplate template, Library library)
    {
        RequireDraft(draft);

        var manifest = ManifestBuilder.Build(template, library, preferred: draft.Manifest);
        var m = StateMigrator.Migrate(draft.State, draft.Dormant, draft.Structure, draft.Structure, manifest, library, null);

        draft.Reconfigure(draft.Structure, template.Id, template.Version, manifest);
        draft.LoadState(m.Active, m.Dormant);
        return new OperationResult(draft, m.Report);
    }

    /// <summary>New analysis at a later valuation date. Template version and table pins carry over.</summary>
    public static OperationResult RollForward(
        Analysis source, DateOnly newValuationDate, DataSnapshot newData,
        GrowthMode growth, AnalysisId newId, string engineVersion, Library library)
    {
        if (newValuationDate <= source.ValuationDate)
            throw new ArgumentException("The new valuation date must be later than the source's.");
        if (newData.Segment != source.Segment || newData.ValuationDate != newValuationDate)
            throw new ArgumentException("The data snapshot must belong to the same segment at the new valuation date.");

        var to = source.Structure.RollTo(newValuationDate, growth);
        RequireValid(to, newValuationDate);
        var stamp = new Provenance(ProvenanceKind.CarriedForward, source.Id, source.Revision);
        var m = StateMigrator.Migrate(source.State, source.Dormant, source.Structure, to, source.Manifest, library, stamp);

        var next = new Analysis(newId, 1, source.Segment, newValuationDate, to,
            source.Template, source.TemplateVersion, source.Manifest, newData, engineVersion)
        {
            DerivedFrom = new Lineage(Operation.RollForward, source.Id, source.Revision)
        };
        next.LoadState(m.Active, m.Dormant);
        return new OperationResult(next, m.Report);
    }

    /// <summary>In place, drafts only. Period lengths and counts may change.</summary>
    public static OperationResult Reshape(Analysis draft, TriangleStructure to, Library library)
    {
        RequireDraft(draft);
        RequireValid(to, draft.ValuationDate);

        var m = StateMigrator.Migrate(draft.State, draft.Dormant, draft.Structure, to, draft.Manifest, library, null);

        draft.Reconfigure(to, draft.Template, draft.TemplateVersion, draft.Manifest);
        draft.LoadState(m.Active, m.Dormant);
        return new OperationResult(draft, m.Report);
    }

    /// <summary>
    /// Copies an analysis onto a segment (the same one or another) at the same valuation date.
    /// The copy is always a Draft. State comes along as carried-forward, so the actuary can review it.
    /// </summary>
    public static OperationResult Duplicate(
        Analysis source, SegmentId targetSegment, DataSnapshot targetData,
        AnalysisId newId, string engineVersion, Library library)
    {
        if (targetData.Segment != targetSegment || targetData.ValuationDate != source.ValuationDate)
            throw new ArgumentException("The data snapshot must belong to the target segment at the same valuation date.");

        var stamp = new Provenance(ProvenanceKind.CarriedForward, source.Id, source.Revision);
        var m = StateMigrator.Migrate(source.State, source.Dormant, source.Structure, source.Structure, source.Manifest, library, stamp);

        var copy = new Analysis(newId, 1, targetSegment, source.ValuationDate, source.Structure,
            source.Template, source.TemplateVersion, source.Manifest, targetData, engineVersion)
        {
            DerivedFrom = new Lineage(Operation.Duplicate, source.Id, source.Revision)
        };
        copy.LoadState(m.Active, m.Dormant);
        return new OperationResult(copy, m.Report);
    }

    /// <summary>
    /// A new Draft revision of the same analysis (typically of a signed-off one). With a new manifest — an upgrade
    /// to newer table versions — state is migrated so values that no longer fit go dormant with a reason.
    /// </summary>
    public static OperationResult NewRevision(
        Analysis source, TemplateVersion templateVersion, ResolvedManifest manifest, string engineVersion, Library library)
    {
        var next = source.NewRevision(templateVersion, manifest, engineVersion);
        var m = StateMigrator.Migrate(source.State, source.Dormant, source.Structure, source.Structure, manifest, library, null);
        next.LoadState(m.Active, m.Dormant);
        return new OperationResult(next, m.Report);
    }

    private static void RequireValid(TriangleStructure s, DateOnly valuation)
    {
        var problems = s.Validate(valuation).ToList();
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
    }

    private static void RequireDraft(Analysis a)
    {
        if (a.Status != AnalysisStatus.Draft)
            throw new InvalidOperationException(
                "This operation edits in place. Create a new revision of a signed-off analysis first.");
    }
}

// ───────────────────────── State migrator ─────────────────────────

internal sealed record MigrationOutcome(
    Dictionary<StateKey, StateEntry> Active,
    Dictionary<StateKey, DormantEntry> Dormant,
    MigrationReport Report);

internal enum StateClass { Amount, Categorical }
internal enum AxisMode { None, Exact, Split, Merge, Lost }

internal static class StateMigrator
{
    /// <param name="carriedFrom">Stamp for a new analysis. Null for in-place operations, which keep each entry's own provenance.</param>
    public static MigrationOutcome Migrate(
        IReadOnlyDictionary<StateKey, StateEntry> active,
        IReadOnlyDictionary<StateKey, DormantEntry> dormant,
        TriangleStructure fromStructure, TriangleStructure toStructure,
        ResolvedManifest manifest, Library library,
        Provenance? carriedFrom)
    {
        // Dormant entries are retried too, so state comes back when its table or period returns.
        var sources = active.Select(kv => (Key: kv.Key, Entry: kv.Value))
            .Concat(dormant.Select(kv => (Key: kv.Key, Entry: kv.Value.Entry)))
            .ToList();

        var toEx = toStructure.ExposureKeys().ToList();
        var toDev = toStructure.DevelopmentKeys().ToList();
        var fromEx = fromStructure.ExposureKeys().ToList();
        var fromDev = fromStructure.DevelopmentKeys().ToList();

        var outActive = new Dictionary<StateKey, StateEntry>();
        var outDormant = new Dictionary<StateKey, DormantEntry>();
        var carried = new List<MigrationEntry>();
        var remapped = new List<MigrationEntry>();
        var parked = new List<MigrationEntry>();
        var fanOuts = new List<(StateKey Src, StateKey Target, StateEntry Entry)>();
        var merges = new Dictionary<StateKey, List<(StateKey Src, StateEntry Entry)>>();

        void Park(StateKey k, StateEntry e, string why)
        {
            outDormant[k] = new DormantEntry(e, why);
            parked.Add(new MigrationEntry(k, why));
        }

        Provenance Stamp(StateEntry e) => carriedFrom ?? e.Provenance;
        Provenance StampRemapped() => new(ProvenanceKind.Remapped, carriedFrom?.From, carriedFrom?.FromRevision);

        foreach (var (key, entry) in sources)
        {
            if (StateValidator.Problem(key, entry.Value, manifest, library) is { } problem)
            {
                Park(key, entry, problem);
                continue;
            }

            var ex = MapAxis(key.Exposure?.Span, toEx, k => k.Span);
            var dv = MapAxis(key.Development?.Span, toDev, k => k.Span);

            if (ex.Mode == AxisMode.Lost || dv.Mode == AxisMode.Lost)
            {
                Park(key, entry, "period does not exist in the new structure");
                continue;
            }

            var newKeys = ex.Targets
                .SelectMany(e => dv.Targets.Select(d => new StateKey(key.Table, key.ColumnKey, e, d)))
                .ToList();

            var exact = (ex.Mode is AxisMode.Exact or AxisMode.None) && (dv.Mode is AxisMode.Exact or AxisMode.None);
            if (exact)
            {
                if (outActive.ContainsKey(newKeys[0]))
                {
                    Park(key, entry, "the period already holds a newer value");
                    continue;
                }
                outActive[newKeys[0]] = new StateEntry(entry.Value, Stamp(entry));
                carried.Add(new MigrationEntry(newKeys[0], "same period"));
                continue;
            }

            // Periods changed length. Amounts cannot be split or merged without weights.
            if (Classify(entry.Value) == StateClass.Amount)
            {
                Park(key, entry, "amounts cannot be split or merged across period lengths");
                continue;
            }

            var merging = ex.Mode == AxisMode.Merge || dv.Mode == AxisMode.Merge;
            foreach (var nk in newKeys)
            {
                if (merging)
                {
                    if (!merges.TryGetValue(nk, out var members)) merges[nk] = members = new();
                    members.Add((key, entry));
                }
                else fanOuts.Add((key, nk, entry));
            }
        }

        // Exact matches win over remaps. A split is all-or-nothing per source: if any target is taken, the source parks.
        foreach (var group in fanOuts.GroupBy(f => f.Src))
        {
            var items = group.ToList();
            if (items.Any(i => outActive.ContainsKey(i.Target)))
            {
                Park(group.Key, items[0].Entry, "a target period already has a value");
                continue;
            }
            foreach (var (_, target, entry) in items)
            {
                outActive[target] = new StateEntry(entry.Value, StampRemapped());
                remapped.Add(new MigrationEntry(target, "split from a longer period"));
            }
        }

        // A merge needs every constituent period present and in agreement.
        foreach (var (nk, members) in merges)
        {
            var expected = CountSources(nk.Exposure, fromEx, toEx, k => k.Span)
                         * CountSources(nk.Development, fromDev, toDev, k => k.Span);
            var covered = Covers(nk.Exposure?.Span, fromEx, k => k.Span) && Covers(nk.Development?.Span, fromDev, k => k.Span);
            var unanimous = members.Select(m => m.Entry.Value).Distinct().Count() == 1;

            if (unanimous && covered && members.Count == expected && !outActive.ContainsKey(nk))
            {
                outActive[nk] = new StateEntry(members[0].Entry.Value, StampRemapped());
                remapped.Add(new MigrationEntry(nk, $"merged from {members.Count} periods"));
            }
            else
            {
                foreach (var (src, entry) in members)
                    Park(src, entry, "merge needs every constituent period to hold the same choice");
            }
        }

        var needs = StateValidator.Missing(outActive, toStructure, manifest, library);
        return new MigrationOutcome(outActive, outDormant, new MigrationReport(carried, remapped, parked, needs));
    }

    private static StateClass Classify(StateValue v) =>
        v is ManualValue ? StateClass.Amount : StateClass.Categorical;

    /// <summary>Maps one source period onto the target periods: same span, split into shorter ones, or merged into a longer one.</summary>
    private static (AxisMode Mode, List<K?> Targets) MapAxis<K>(
        MonthSpan? source, List<K> targets, Func<K, MonthSpan> span) where K : struct
    {
        if (source is not { } s) return (AxisMode.None, new List<K?> { null });

        var exact = targets.Where(t => span(t) == s).ToList();
        if (exact.Count == 1) return (AxisMode.Exact, new List<K?> { exact[0] });

        var inside = targets.Where(t => s.Contains(span(t))).ToList();          // source covers several targets
        if (inside.Count > 0) return (AxisMode.Split, inside.Select(t => (K?)t).ToList());

        var outside = targets.Where(t => span(t).Contains(s)).ToList();         // one target covers the source
        if (outside.Count == 1) return (AxisMode.Merge, new List<K?> { outside[0] });

        return (AxisMode.Lost, new List<K?>());
    }

    /// <summary>True when the source periods tile the whole target period (no constituent is missing from the old structure).</summary>
    private static bool Covers<K>(MonthSpan? target, List<K> sourceKeys, Func<K, MonthSpan> span) where K : struct
    {
        if (target is not { } t) return true;
        return sourceKeys.Select(span).Where(t.Contains).Sum(x => x.Length) == t.Length;
    }

    /// <summary>How many source periods feed this target period (1 for an axis the key doesn't have).</summary>
    private static int CountSources<K>(K? target, List<K> sourceKeys, List<K> targets, Func<K, MonthSpan> span)
        where K : struct
    {
        if (target is null) return 1;
        return sourceKeys.Count(sk => MapAxis(span(sk), targets, span).Targets.Contains(target));
    }
}
