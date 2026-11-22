using Actuarial.Application.Data;
using Actuarial.Application.Persistence;
using Actuarial.Application.Seed;
using Actuarial.Application.UseCases;
using Actuarial.Domain;
using Microsoft.JSInterop;

namespace Actuarial.Web.Services;

public enum Section { Dashboard, Library, Templates, Data, Analyses, Analysis, Audit }

public sealed record Toast(Guid Id, string Message, bool IsError);

/// <summary>
/// Client-side application state: the service over the workspace, navigation, and persistence to localStorage.
/// The whole app is static files; the browser is the only store.
/// </summary>
public sealed class AppState
{
    private const string StorageKey = "actuarial-workspace";
    private readonly IJSInProcessRuntime _js;
    private readonly ClaimWarehouse _warehouse = ClaimGenerator.Generate();

    public ActuarialService Service { get; }
    public event Action? OnChange;

    public Section Section { get; private set; } = Section.Dashboard;
    public AnalysisId? AnalysisId { get; private set; }
    public int AnalysisRevision { get; private set; }
    public TableId? FocusTable { get; set; }
    public TableId? LibraryTable { get; set; }
    public TemplateId? LibraryTemplate { get; set; }
    public MigrationReport? LastReport { get; set; }
    public string? LastReportTitle { get; set; }
    public List<Toast> Toasts { get; } = new();
    public string? StorageWarning { get; private set; }
    public long StoredBytes { get; private set; }
    /// <summary>Incremented on every change to the workspace; views cache computed results against it.</summary>
    public int Version { get; private set; }

    /// <summary>True on phones (narrow viewport or touch-first mobile device). Pages switch to their mobile layout.</summary>
    public bool IsMobile { get; private set; }

    [JSInvokable]
    public void SetMobile(bool mobile)
    {
        if (IsMobile == mobile) return;
        IsMobile = mobile;
        Changed();
    }

    public AppState(IJSInProcessRuntime js)
    {
        _js = js;
        try
        {
            IsMobile = js.Invoke<bool>("actuarial.isMobile");
            js.InvokeVoid("actuarial.watchDevice", DotNetObjectReference.Create(this));
        }
        catch (JSException) { IsMobile = false; }
        Service = new ActuarialService(LoadOrSeed());
        Service.Changed += () => { Version++; Save(); };
    }

    public Workspace Workspace => Service.Workspace;

    private Workspace LoadOrSeed()
    {
        try
        {
            var json = _js.Invoke<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json))
                return Json.Deserialize<WorkspaceDocument>(json).ToWorkspace(_warehouse);
        }
        catch (Exception e)
        {
            StorageWarning = $"The saved workspace could not be read ({e.Message}); a fresh demo workspace was loaded.";
        }
        var w = DemoSeed.Build();
        Persist(w);
        return w;
    }

    private void Save() => Persist(Workspace);

    private void Persist(Workspace w)
    {
        try
        {
            var json = Json.Serialize(WorkspaceDocument.From(w));
            StoredBytes = json.Length;
            _js.InvokeVoid("localStorage.setItem", StorageKey, json);
        }
        catch (Exception e)
        {
            StorageWarning = $"Changes could not be saved in this browser ({e.Message}). Export the workspace to keep them.";
        }
    }

    public void ResetDemo()
    {
        Service.Replace(DemoSeed.Build());
        LastReport = null;
        Go(Section.Dashboard);
        Toast("The demo workspace was rebuilt.");
    }

    public string ExportJson() => Json.Serialize(WorkspaceDocument.From(Workspace), pretty: true);

    public void ImportJson(string json)
    {
        var w = Json.Deserialize<WorkspaceDocument>(json).ToWorkspace(_warehouse);
        Service.Replace(w);
        Go(Section.Dashboard);
        Toast("Workspace imported.");
    }

    public void Download(string fileName, string content) => _js.InvokeVoid("actuarial.download", fileName, content);

    // ───── navigation ─────

    public void Go(Section s)
    {
        Section = s;
        Changed();
        _js.InvokeVoid("window.scrollTo", 0, 0);
    }

    public void OpenAnalysis(AnalysisId id, int revision, TableId? focus = null)
    {
        AnalysisId = id;
        AnalysisRevision = revision;
        if (focus is not null) FocusTable = focus;
        Go(Section.Analysis);
    }

    public void OpenLibraryTable(TableId id)
    {
        LibraryTable = id;
        Go(Section.Library);
    }

    public void ShowReport(string title, MigrationReport report)
    {
        LastReport = report;
        LastReportTitle = title;
    }

    // ───── feedback ─────

    public void Toast(string message, bool error = false)
    {
        var t = new Toast(Guid.NewGuid(), message, error);
        Toasts.Add(t);
        if (Toasts.Count > 4) Toasts.RemoveAt(0);
        Changed();
        _ = DismissLater(t);
    }

    private async Task DismissLater(Toast t)
    {
        await Task.Delay(t.IsError ? 8000 : 4000);
        Toasts.Remove(t);
        Changed();
    }

    /// <summary>Runs a use case and turns refusals into a visible message.</summary>
    public bool Try(Action action, string? success = null)
    {
        try
        {
            action();
            if (success is not null) Toast(success);
            else Changed();
            return true;
        }
        catch (UseCaseException e) { Toast(e.Message, true); }
        catch (InvalidOperationException e) { Toast(e.Message, true); }
        catch (ArgumentException e) { Toast(e.Message, true); }
        return false;
    }

    public void Changed() => OnChange?.Invoke();
}
