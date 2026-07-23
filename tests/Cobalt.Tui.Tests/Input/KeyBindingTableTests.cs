using Cobalt.Core.Config;
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

    // ---- FromConfig: [keys.<scope>] overrides (ticket #30) ----

    private static KeysConfig Keys(string scope, params (string Command, string Sequences)[] commands)
    {
        var scopeCommands = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (command, sequences) in commands)
        {
            scopeCommands[command] = sequences.Length == 0 ? [] : [sequences];
        }
        return new KeysConfig(new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            [scope] = scopeCommands,
        });
    }

    [Fact]
    public void FromConfig_Global_Alias_Matches_From_Every_Scope()
    {
        // UAT repro: [keys.global] move-down = ["j", "n"] — the new alias must fall through
        // into the modal scopes (PR detail, diff review) exactly like the default "j" does.
        var keys = new KeysConfig(new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["global"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["move-down"] = ["j", "n"],
            },
        });
        var router = new KeymapRouter(KeyBindingTable.FromConfig(keys));

        foreach (var scope in Enum.GetValues<KeyScope>())
        {
            var result = router.Feed("n", scope);
            Assert.Equal(KeyResultKind.Matched, result.Kind);
            // A scope's own default still shadows a global remap — deliberately, the same
            // precedence that lets diff review's Tab mean cycle-pane: "n" stays search-next
            // there. Every other scope sees the new global alias.
            Assert.Equal(
                scope == KeyScope.DiffReview ? AppCommand.SearchNext : AppCommand.MoveDown,
                result.Command);
            router.Reset();
        }
    }

    [Theory]
    [InlineData("5j")]       // one two-char token — the tokenizer emits "5" then "j"
    public void FromConfig_Rejects_A_Token_The_Tokenizer_Can_Never_Emit(string token)
    {
        // UAT repro: a token no keypress ever produces makes the binding permanently dead
        // and, worse, silently replaces the command's defaults. Must fail startup instead.
        var ex = Assert.Throws<ConfigException>(
            () => KeyBindingTable.FromConfig(Keys("global", ("move-down", token))));

        Assert.Contains(token, ex.Message);
    }

    [Theory]
    [InlineData(0x01)] // a TOML-escaped control char: a single rune, but never printable
    [InlineData(0x7F)] // DEL - outside the tokenizer's printable range
    public void FromConfig_Rejects_A_Control_Rune_Token(int codePoint)
    {
        // Review finding: the tokenizer only emits runes TryGetPrintableRune accepts, so an
        // escaped control character is the same dead-binding class as "5j" through a side door.
        var token = ((char)codePoint).ToString();

        var ex = Assert.Throws<ConfigException>(
            () => KeyBindingTable.FromConfig(Keys("global", ("move-down", token))));

        Assert.Contains("keypress", ex.Message);
    }

    [Theory]
    [InlineData("refresh", "C-r")]     // control chord
    [InlineData("open", "Enter")]      // named key
    [InlineData("prev-tab", "S-Tab")]  // shifted named key
    [InlineData("move-down", "Down")]  // cursor named key
    [InlineData("refresh", "g r")]     // multi-token sequence
    [InlineData("refresh", "é")]       // printable non-ASCII rune
    [InlineData("refresh", "𝄞")]       // astral-plane rune (surrogate pair, still one rune)
    public void FromConfig_Accepts_Every_Token_Shape_The_Tokenizer_Emits(string command, string sequence)
    {
        // Chords, named keys, and multi-token sequences are all real tokenizer output
        // (sequences chosen not to collide with the remaining defaults).
        _ = KeyBindingTable.FromConfig(Keys("global", (command, sequence)));
    }

    [Fact]
    public void FromConfig_With_No_Overrides_Matches_The_Default_Table_In_Every_Scope()
    {
        var table = KeyBindingTable.FromConfig(KeysConfig.Empty);
        var defaults = KeyBindingTable.Default();

        foreach (var scope in Enum.GetValues<KeyScope>())
        {
            Assert.Equal(
                defaults.Visible(scope).Select(b => (string.Join(' ', b.Sequence), b.Command)),
                table.Visible(scope).Select(b => (string.Join(' ', b.Sequence), b.Command)));
        }
    }

    [Fact]
    public void FromConfig_Overrides_A_Default_Binding()
    {
        var table = KeyBindingTable.FromConfig(Keys("global", ("move-down", "n")));

        var moveDown = table.Visible(KeyScope.Global).Where(b => b.Command == AppCommand.MoveDown).ToList();
        Assert.Equal([new[] { "n" }], moveDown.Select(b => b.Sequence));
    }

    [Fact]
    public void FromConfig_Empty_String_Unbinds_A_Command()
    {
        var table = KeyBindingTable.FromConfig(Keys("global", ("move-down", "")));

        Assert.DoesNotContain(table.Visible(KeyScope.Global), b => b.Command == AppCommand.MoveDown);
    }

    [Fact]
    public void FromConfig_Extends_A_Scope_With_A_New_Binding_For_An_Existing_Command()
    {
        // MarkViewed is only bound in DiffReview by default; extend it into WorkItemList.
        var table = KeyBindingTable.FromConfig(Keys("workitemlist", ("mark-viewed", "Q")));

        Assert.Contains(table.Visible(KeyScope.WorkItemList), b => b.Sequence is ["Q"] && b.Command == AppCommand.MarkViewed);
    }

    [Fact]
    public void FromConfig_Unknown_Command_Throws_ConfigException()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("global", ("not-a-real-command", "n"))));

        Assert.Contains("not-a-real-command", ex.Message);
    }

    [Fact]
    public void FromConfig_Unknown_Scope_Throws_ConfigException()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("not-a-real-scope", ("move-down", "n"))));

        Assert.Contains("not-a-real-scope", ex.Message);
    }

    [Fact]
    public void FromConfig_Duplicate_Sequence_Conflict_Throws_ConfigException_Naming_The_Sequence()
    {
        // move-down keeps its default "j"; rebinding move-up onto "j" too is a same-scope
        // shadow conflict (unlike scoped-over-global shadowing, which is legal).
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("global", ("move-up", "j"))));

        Assert.Contains("j", ex.Message);
    }

    [Fact]
    public void FromConfig_Prefix_Conflict_Throws_ConfigException_Naming_The_Sequence()
    {
        // "g" would shadow the existing "g g" (MoveTop) sequence, making it unreachable.
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("global", ("refresh", "g"))));

        Assert.Contains("g", ex.Message);
    }

    [Fact]
    public void FromConfig_Duplicate_Sequence_Conflict_In_A_Non_Global_Scope_Throws()
    {
        // ThreadView's default "x" belongs to ResolveThread; rebinding Comment onto "x"
        // too is a same-scope shadow conflict (not the legal scoped-over-global kind).
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("threadview", ("comment", "x"))));

        Assert.Contains("x", ex.Message);
    }

    [Fact]
    public void FromConfig_Sequence_Containing_Esc_Throws_ConfigException()
    {
        // KeymapRouter always treats "Esc" as cancel before consulting the binding
        // table; a sequence containing it could never fire.
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("global", ("refresh", "g Esc"))));

        Assert.Contains("Esc", ex.Message);
        Assert.Contains("refresh", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromConfig_Sequence_Starting_With_A_Digit_Throws_ConfigException()
    {
        // KeymapRouter always consumes a leading digit as a count prefix before
        // consulting the binding table; such a sequence could never fire.
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("global", ("refresh", "5"))));

        Assert.Contains("refresh", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromConfig_Command_Name_With_Extra_Hyphen_Is_Unknown()
    {
        // Only an exact kebab-case match resolves now; stripping every hyphen used to
        // let a typo like "move--down" silently match "move-down".
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("global", ("move--down", "n"))));

        Assert.Contains("move--down", ex.Message);
    }

    [Fact]
    public void FromConfig_Two_Config_Keys_Resolving_To_The_Same_Command_Throws()
    {
        // "refresh" and "Refresh" are distinct TOML keys but the same AppCommand
        // case-insensitively; last-wins would silently drop one binding.
        var ex = Assert.Throws<ConfigException>(() =>
            KeyBindingTable.FromConfig(Keys("global", ("refresh", "r"), ("Refresh", "x"))));

        Assert.Contains("refresh", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
