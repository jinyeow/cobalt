using Cobalt.Core.Ado;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// The <c>:log</c> dialog's source of truth: a bounded ring of recent
/// <see cref="AdoOperation"/> records fed by <see cref="Cobalt.Core.Ado.AdoHttp.OperationObserver"/>.
/// Mirrors <see cref="MessageLog"/>'s shape (bounded ring + <see cref="Changed"/>).
/// </summary>
public sealed class OperationLog(int capacity = 200)
{
    private readonly List<AdoOperation> _history = [];

    public event Action? Changed;

    public IReadOnlyList<AdoOperation> History => _history;

    public void Add(AdoOperation operation)
    {
        _history.Add(operation);
        if (_history.Count > capacity)
        {
            _history.RemoveRange(0, _history.Count - capacity);
        }
        Changed?.Invoke();
    }
}
