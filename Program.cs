using System; using System.Linq; using System.Reflection;
var asm=typeof(Terminal.Gui.App.Application).Assembly;
var ic=asm.GetTypes().First(t=>t.Name=="IClipboard");
foreach(var m in ic.GetMembers()) Console.WriteLine(m.MemberType+" "+m);
