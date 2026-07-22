namespace Cobalt.Tui.ViewModels;

public enum MessageLevel
{
    Info,
    Error,
}

public sealed record Message(MessageLevel Level, string Text, DateTimeOffset At);

/// <summary>
/// The status/error bar's source of truth plus the `:messages` history — errors
/// surface here instead of stack traces (SPEC §6).
/// </summary>
public sealed class MessageLog(int capacity = 200, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<Message> _history = [];

    /// <summary>
    /// Raised when the log changes. May fire on a threadpool continuation (an ADO call completing),
    /// so a subscriber that touches Terminal.Gui must marshal onto the UI thread via
    /// <see cref="App.IUiPost"/> — never <c>IApplication</c>, which this UI-free view-model (ADR 0004)
    /// deliberately does not reference.
    /// </summary>
    public event Action? Changed;

    public Message? Current { get; private set; }

    public IReadOnlyList<Message> History => _history;

    public void Info(string text) => Add(new Message(MessageLevel.Info, text, _time.GetUtcNow()));

    public void Error(string text) => Add(new Message(MessageLevel.Error, text, _time.GetUtcNow()));

    private void Add(Message message)
    {
        Current = message;
        _history.Add(message);
        if (_history.Count > capacity)
        {
            _history.RemoveRange(0, _history.Count - capacity);
        }
        Changed?.Invoke();
    }
}
