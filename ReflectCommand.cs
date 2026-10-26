using System.Reflection;

namespace LexExport;

// Prints the public members of every LegendaryExplorerCore type whose full name contains a
// filter. LEX has no API docs; this is the fastest way to find a binary converter's fields.
public static class ReflectCommand
{
    public static int Run(string filter)
    {
        Assembly lex = typeof(LegendaryExplorerCore.Packages.IMEPackage).Assembly;
        foreach (Type t in lex.GetTypes().Where(t => t.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"== {t.FullName}");
            foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Console.WriteLine($"   {m.MemberType} {m}");
        }
        return 0;
    }
}
