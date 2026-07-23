namespace Cobalt.Tui.Tasks;

/// <summary>
/// An atomic publish/read pair for view-model state: one volatile reference holding one
/// immutable <typeparamref name="T"/>. Generalizes the <c>PrDiffViewModel.DiffState</c>
/// pattern (ADR 0008): related values that must never tear live together inside a single
/// <c>sealed record</c>, published by a single reference write and read back by a single
/// reference read. Consumers snapshot <see cref="Current"/> once and destructure — reading
/// two properties through two separate reads would re-introduce the tear on the read side
/// (the ADR 0008 amendment behind <c>CurrentDiffSnapshot</c>).
/// </summary>
public sealed class Published<T> where T : class
{
    private T? _current;

    /// <summary>The last published value, from a single volatile read; null before the first publish.</summary>
    public T? Current => Volatile.Read(ref _current);

    /// <summary>Replaces the published value in a single volatile write; null clears it.</summary>
    public void Publish(T? value) => Volatile.Write(ref _current, value);
}
