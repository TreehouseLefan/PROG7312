using Microsoft.AspNetCore.Components;
using SmartX.Client.Services;

namespace SmartX.Client.Components;

/// <summary>
/// Base class for views that render from <see cref="AppState"/>: it re-renders the component whenever the
/// shared state changes and unsubscribes when the component is removed, so leaving a page never leaks
/// handlers and returning to it shows the latest data immediately.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    [Inject]
    protected AppState State { get; set; } = default!;

    [Inject]
    protected SmartXApiClient Api { get; set; } = default!;

    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
    }

    private void OnStateChanged() => _ = InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        State.Changed -= OnStateChanged;
    }
}
