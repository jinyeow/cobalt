using System.Reflection;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// Permanently locks ADR 0004: nothing in the <c>Cobalt.Tui.ViewModels</c> namespace may expose or
/// hold a Terminal.Gui type. This is the reflection backstop behind M2's IUiPost seam — the reason a
/// view-model marshals through <c>IUiPost</c> and never sees <c>IApplication</c>.
/// </summary>
public class ViewModelPurityTests
{
    private static readonly Assembly TerminalGui = typeof(IApplication).Assembly;
    private static readonly Assembly Tui = typeof(PrListViewModel).Assembly;

    private static IEnumerable<Type> ViewModelTypes() =>
        Tui.GetTypes().Where(t => t.Namespace == "Cobalt.Tui.ViewModels");

    /// <summary>True if <paramref name="type"/> (or any generic argument, recursively) lives in Terminal.Gui.</summary>
    private static bool IsTerminalGui(Type type)
    {
        var t = type;
        while (t.IsByRef || t.IsArray || t.IsPointer)
        {
            t = t.GetElementType()!;
        }
        if (t.Assembly == TerminalGui)
        {
            return true;
        }
        return t.IsGenericType && t.GetGenericArguments().Any(IsTerminalGui);
    }

    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [Fact]
    public void No_ViewModel_Constructor_Parameter_Is_A_Terminal_Gui_Type()
    {
        var offenders =
            (from type in ViewModelTypes()
             from ctor in type.GetConstructors(AllMembers)
             from p in ctor.GetParameters()
             where IsTerminalGui(p.ParameterType)
             select $"{type.Name}.ctor({p.ParameterType.Name} {p.Name})").ToList();

        Assert.True(offenders.Count == 0, "Terminal.Gui in a view-model constructor: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_ViewModel_Field_Is_A_Terminal_Gui_Type()
    {
        var offenders =
            (from type in ViewModelTypes()
             from f in type.GetFields(AllMembers)
             where IsTerminalGui(f.FieldType)
             select $"{type.Name}.{f.Name}: {f.FieldType.Name}").ToList();

        Assert.True(offenders.Count == 0, "Terminal.Gui in a view-model field: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_ViewModel_Property_Is_A_Terminal_Gui_Type()
    {
        var offenders =
            (from type in ViewModelTypes()
             from p in type.GetProperties(AllMembers)
             where IsTerminalGui(p.PropertyType)
             select $"{type.Name}.{p.Name}: {p.PropertyType.Name}").ToList();

        Assert.True(offenders.Count == 0, "Terminal.Gui in a view-model property: " + string.Join(", ", offenders));
    }
}
