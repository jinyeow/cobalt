using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PaletteSuggestionsViewModelTests
{
    private static PaletteSuggestionsViewModel Vm(
        IReadOnlyList<string>? contexts = null, IReadOnlyList<string>? projects = null) =>
        new(() => contexts ?? [], () => projects ?? []);

    [Fact]
    public void Unique_Prefix_Match_Becomes_Current()
    {
        var vm = Vm();

        vm.SetInput("th");

        Assert.Equal("theme", vm.Current);
    }

    [Fact]
    public void Accept_Completes_Command_With_No_Argument_Provider_Without_Trailing_Space()
    {
        var vm = Vm();

        vm.SetInput("th");

        Assert.Equal("theme", vm.Accept());
    }

    [Fact]
    public void Accept_Completes_Command_With_An_Argument_Provider_With_Trailing_Space()
    {
        var vm = Vm();

        vm.SetInput("con");

        Assert.Equal("context", vm.Current);
        Assert.Equal("context ", vm.Accept());
    }

    [Fact]
    public void Empty_Input_Lists_The_Whole_Catalog_And_Cycle_Wraps()
    {
        var vm = Vm();

        vm.SetInput("");
        var first = vm.Current;

        for (var i = 0; i < PaletteCommandParser.Catalog.Count; i++)
        {
            vm.CycleNext();
        }

        Assert.Equal(first, vm.Current);
    }

    [Fact]
    public void CyclePrev_From_First_Wraps_To_Last()
    {
        var vm = Vm();
        vm.SetInput("");
        var last = PaletteCommandParser.Catalog[^1].Name;

        vm.CyclePrev();

        Assert.Equal(last, vm.Current);
    }

    [Fact]
    public void Unknown_Prefix_Has_No_Current_And_Accept_Leaves_Input_Unchanged()
    {
        var vm = Vm();

        vm.SetInput("zzz");

        Assert.Null(vm.Current);
        Assert.Equal("zzz", vm.Accept());
    }

    [Fact]
    public void Fuzzy_Subsequence_Matches_When_No_Prefix_Matches()
    {
        var vm = Vm();

        vm.SetInput("tm"); // t...m subsequence of "theme", not a prefix of anything

        Assert.Equal("theme", vm.Current);
    }

    [Fact]
    public void Context_Argument_Completes_Against_Real_Names()
    {
        var vm = Vm(contexts: ["work", "oss"]);

        vm.SetInput("context w");

        Assert.Equal("work", vm.Current);
        Assert.Equal("context work", vm.Accept());
    }

    [Fact]
    public void Bare_Command_With_Trailing_Space_Lists_All_Provider_Values_And_Cycles()
    {
        var vm = Vm(contexts: ["work", "oss"]);

        vm.SetInput("context ");
        Assert.Equal("work", vm.Current);

        vm.CycleNext();
        Assert.Equal("oss", vm.Current);
    }

    [Fact]
    public void Project_Argument_Completes_Against_Real_Project_Names()
    {
        var vm = Vm(projects: ["Fabrikam", "Contoso"]);

        vm.SetInput("project Fab");

        Assert.Equal("Fabrikam", vm.Current);
        Assert.Equal("project Fabrikam", vm.Accept());
    }

    [Fact]
    public void Argument_For_A_Command_Without_A_Provider_Has_No_Candidates()
    {
        var vm = Vm();

        vm.SetInput("theme d");

        Assert.Null(vm.Current);
        Assert.Equal("theme d", vm.Accept());
    }

    [Fact]
    public void Accept_Preserves_A_Leading_Colon_On_A_Successful_Completion()
    {
        var vm = Vm();

        vm.SetInput(":th");

        Assert.Equal(":theme", vm.Accept());
    }

    [Fact]
    public void Accept_Preserves_A_Leading_Colon_On_An_Unmatched_Completion()
    {
        var vm = Vm();

        vm.SetInput(":zzz");

        Assert.Equal(":zzz", vm.Accept());
    }

    [Fact]
    public void Double_Space_Before_The_Argument_Still_Completes()
    {
        // PaletteCommandParser.Parse tolerates extra whitespace (TrimEntries); completion must too.
        var vm = Vm(contexts: ["work", "oss"]);

        vm.SetInput("context  w");

        Assert.Equal("work", vm.Current);
        Assert.Equal("context work", vm.Accept());
    }
}
