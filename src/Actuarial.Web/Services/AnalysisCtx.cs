using Actuarial.Application.UseCases;
using Actuarial.Domain;

namespace Actuarial.Web.Services;

/// <summary>What a table view needs: the computed analysis plus the state operations, routed through the use cases.</summary>
public sealed class AnalysisCtx(AppState app, AnalysisView view)
{
    public AppState App { get; } = app;
    public AnalysisView View { get; } = view;
    public Analysis A => View.Analysis;
    public bool Editable => A.Status == AnalysisStatus.Draft && App.Service.IsLatestRevision(A);

    public IReadOnlyList<ExposureKey> Ex => View.Exposures;
    public IReadOnlyList<DevKey> Dev => View.Developments;
    public int Latest(int exposure) => View.LatestDevIndex[exposure];

    public TableDefinition Def(TableId id) => App.Service.Library.Get(id, A.Manifest.Pinned[id]);
    public string Name(TableId id) => A.Manifest.Pinned.ContainsKey(id) ? Def(id).Name : App.Service.TableName(id);
    public TableResult? Result(TableId id) => View.Results.Tables.GetValueOrDefault(id);
    public StateEntry? Entry(StateKey k) => A.State.GetValueOrDefault(k);
    public bool Needs(StateKey k) => View.NeedsInput.Contains(k);

    public void Select(StateKey k, StateValue v) => App.Try(() => App.Service.Select(A, k, v));
    public void SelectMany(IEnumerable<StateKey> keys, StateValue v, string what) =>
        App.Try(() => { var n = App.Service.SelectMany(A, keys, v); App.Toast($"{what}: {n} updated."); });
    public void Clear(StateKey k) => App.Try(() => App.Service.Clear(A, k));

    /// <summary>Typed amount: blank clears the entry, a number stores a ManualValue.</summary>
    public void SetAmount(StateKey k, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { if (A.State.ContainsKey(k)) Clear(k); return; }
        if (Fmt.TryParse(text, out var v)) Select(k, new ManualValue(v));
        else App.Toast($"'{text}' is not a number.", true);
    }

    public decimal? Manual(StateKey k) => Entry(k)?.Value is ManualValue m ? m.Value : null;
}
