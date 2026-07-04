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
}
