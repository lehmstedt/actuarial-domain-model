using Microsoft.AspNetCore.Components;

namespace Actuarial.Web.Services;

/// <summary>
/// Base for pages: re-renders whenever the workspace or navigation changes. Parameterless components are not
/// re-rendered by their parent, so without this a page would keep showing results computed before an edit.
/// </summary>
public abstract class AppComponent : ComponentBase, IDisposable
{
    [Inject] protected AppState App { get; set; } = null!;

    protected override void OnInitialized()
    {
        App.OnChange += Refresh;
        OnStarted();
    }

    protected virtual void OnStarted() { }

    private void Refresh() => InvokeAsync(StateHasChanged);

    public void Dispose() => App.OnChange -= Refresh;
}
