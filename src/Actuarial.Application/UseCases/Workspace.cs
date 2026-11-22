using Actuarial.Application.Data;
using Actuarial.Domain;

namespace Actuarial.Application.UseCases;

public sealed record AnalysisRecord(Analysis Analysis, DateTimeOffset CreatedAt, string CreatedBy);

public enum AuditKind
{
    TablesPublished, PublishRejected, TemplatePublished, SegmentCreated, SnapshotTaken,
    AnalysisCreated, TemplateApplied, RolledForward, Reshaped, Duplicated, NewRevision, SignedOff, Verified
}

/// <summary>Persisted audit trail. Operations keep their migration report here, so what was carried is reviewable later.</summary>
public sealed record AuditEvent(
    Guid Id, DateTimeOffset At, string User, AuditKind Kind, string Summary,
    AnalysisId? Analysis = null, int? Revision = null, MigrationReport? Report = null, IReadOnlyList<string>? Details = null);

/// <summary>Everything the application holds. The definition side is the two libraries; the instance side the analyses.</summary>
public sealed class Workspace
{
    public Workspace(ClaimWarehouse warehouse) => Warehouse = warehouse;

    public ClaimWarehouse Warehouse { get; }
    public string User { get; set; } = "actuary";
    public Library Library { get; set; } = new();
    public TemplateLibrary Templates { get; set; } = new();
    public List<Segment> Segments { get; } = new();
    public Dictionary<Guid, SnapshotData> Snapshots { get; } = new();
    public List<AnalysisRecord> Analyses { get; } = new();
    public List<AuditEvent> Audit { get; } = new();
}
