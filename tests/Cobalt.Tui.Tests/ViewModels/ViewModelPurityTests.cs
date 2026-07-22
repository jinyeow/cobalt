using System.Reflection;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// Reflection backstop for ADR 0004: no Terminal.Gui type may appear in the <em>signature</em> of any
/// type in the <c>Cobalt.Tui.ViewModels</c> namespace — no ctor parameter, field, property, method
/// parameter or return type, base type, or implemented interface. It is signature-level only: it does
/// not scan method bodies, so a view-model could still construct a Terminal.Gui type internally (the
/// three-project layering keeps the reference out entirely). This is the backstop behind M2's IUiPost
/// seam — the reason a view-model marshals through <c>IUiPost</c> and never sees <c>IApplication</c>.
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

    [Fact]
    public void No_ViewModel_Method_Signature_Is_A_Terminal_Gui_Type()
    {
        // Property accessors and event add/remove surface as methods too; their types are already
        // covered by the property/field sweeps, but including them here is harmless and keeps the
        // sweep exhaustive over the method table.
        var offenders =
            (from type in ViewModelTypes()
             from m in type.GetMethods(AllMembers)
             let hits =
                 m.GetParameters().Where(p => IsTerminalGui(p.ParameterType)).Select(p => $"{p.ParameterType.Name} {p.Name}")
                  .Concat(IsTerminalGui(m.ReturnType) ? [$"returns {m.ReturnType.Name}"] : Array.Empty<string>())
             from hit in hits
             select $"{type.Name}.{m.Name}({hit})").ToList();

        Assert.True(offenders.Count == 0, "Terminal.Gui in a view-model method signature: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_ViewModel_Base_Type_Or_Interface_Is_A_Terminal_Gui_Type()
    {
        var offenders = new List<string>();
        foreach (var type in ViewModelTypes())
        {
            for (var b = type.BaseType; b is not null; b = b.BaseType)
            {
                if (IsTerminalGui(b))
                {
                    offenders.Add($"{type.Name} : {b.Name}");
                }
            }
            offenders.AddRange(type.GetInterfaces().Where(IsTerminalGui).Select(i => $"{type.Name} : {i.Name}"));
        }

        Assert.True(offenders.Count == 0, "Terminal.Gui in a view-model base type or interface: " + string.Join(", ", offenders));
    }
}
