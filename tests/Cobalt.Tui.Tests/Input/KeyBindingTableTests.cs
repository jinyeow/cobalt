using Cobalt.Tui.Input;

namespace Cobalt.Tui.Tests.Input;

public class KeyBindingTableTests
{
    [Fact]
    public void Default_Table_Passes_Validation()
    {
        // Default() calls Validate(); reaching here means no prefix collisions.
        var table = KeyBindingTable.Default();
        table.Validate();
    }

    [Fact]
    public void Validate_Rejects_A_Prefix_Collision()
    {
        var table = new KeyBindingTable();
        table.Bind(KeyScope.Global, "g", AppCommand.MoveTop);
        table.Bind(KeyScope.Global, "g g", AppCommand.MoveBottom); // 'g' is a prefix of 'g g'

        Assert.Throws<InvalidOperationException>(table.Validate);
    }

    [Fact]
    public void Validate_Detects_Cross_Scope_Collision()
    {
        var table = new KeyBindingTable();
        table.Bind(KeyScope.Global, "y y", AppCommand.YankId);
        table.Bind(KeyScope.WorkItemList, "y", AppCommand.Comment); // scoped 'y' shadows global 'y y'

        Assert.Throws<InvalidOperationException>(table.Validate);
    }

    // ---- INPUT-4: a shared immutable default, so call sites needn't each build their own ----

    [Fact]
    public void Shared_Returns_The_Same_Instance_Every_Time()
    {
        Assert.Same(KeyBindingTable.Shared, KeyBindingTable.Shared);
    }

    [Fact]
    public void Shared_Is_A_Valid_Default_Table()
    {
        Assert.Equal(AppCommand.MoveDown, KeyBindingTable.Shared.Visible(KeyScope.Global)
            .First(b => b.Sequence is ["j"]).Command);
    }

    // ---- INPUT-2: per-scope binding arrays are cached, not rebuilt on every router lookup ----

    [Fact]
    public void Visible_Returns_The_Same_Array_On_Repeated_Calls()
    {
        var table = KeyBindingTable.Default();

        var first = table.Visible(KeyScope.WorkItemList);
        var second = table.Visible(KeyScope.WorkItemList);

        Assert.Same(first, second);
    }

    [Fact]
    public void Binding_After_A_Cached_Read_Invalidates_The_Cache()
    {
        var table = new KeyBindingTable();
        table.Bind(KeyScope.Global, "a", AppCommand.MoveDown);
        var before = table.Visible(KeyScope.Global); // caches it

        table.Bind(KeyScope.Global, "b", AppCommand.MoveUp);
        var after = table.Visible(KeyScope.Global);

        Assert.NotSame(before, after);
        Assert.Contains(after, b => b.Sequence is ["b"]);
    }
}
