using Cobalt.Core.Ado;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// The <c>:log</c> dialog's source of truth: a bounded ring of recent
/// <see cref="AdoOperation"/> records fed by <see cref="Cobalt.Core.Ado.AdoHttp.OperationObserver"/>.
/// Mirrors <see cref="MessageLog"/>'s shape (bounded ring + <see cref="Changed"/>).
/// </summary>
public sealed class OperationLog(int capacity = 200)
{
    private readonly object _lock = new();
    private readonly List<AdoOperation> _history = [];

    public event Action? Changed;

    /// <summary>A snapshot, not the live list — the observer that feeds <see cref="Add"/> can
    /// fire from threadpool continuation threads (AdoHttp's <c>ConfigureAwait(false)</c>).</summary>
    public IReadOnlyList<AdoOperation> History
    {
        get
        {
            lock (_lock)
            {
                return _history.ToArray();
            }
        }
    }

    public void Add(AdoOperation operation)
    {
        lock (_lock)
        {
            _history.Add(operation);
            if (_history.Count > capacity)
            {
                _history.RemoveRange(0, _history.Count - capacity);
            }
        }
        Changed?.Invoke();
    }
}
