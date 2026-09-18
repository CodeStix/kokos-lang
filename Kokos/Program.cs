using Kokos.Compiler.Parsing;
using Kokos.Compiler.Syntax;

namespace Kokos;

internal class Program
{
    private const string ExampleSource = """
        function exampleFunc(args: [String], num: Int): Object {
            let exampleVar = args.join(".");
            return exampleVar + num.toString();
        }
        """;

    static void Main(string[] args)
    {
        var source = args.Length > 0 ? File.ReadAllText(args[0]) : ExampleSource;

        var unit = KokosParser.Parse(source, out var diagnostics);

        Console.WriteLine("=== AST ===");
        DumpNode(unit, 0);

        if (diagnostics.Any())
        {
            Console.WriteLine();
            Console.WriteLine("=== Diagnostics ===");
            foreach (var diagnostic in diagnostics)
                Console.WriteLine(diagnostic);
        }

        var roundTripped = unit.GetFullText();
        var matches = roundTripped == source;

        Console.WriteLine();
        Console.WriteLine($"=== Round-trip {(matches ? "OK" : "MISMATCH")} ===");
        if (!matches)
        {
            Console.WriteLine("--- original ---");
            Console.WriteLine(source);
            Console.WriteLine("--- reconstructed ---");
            Console.WriteLine(roundTripped);
        }
    }

    private static void DumpNode(KokosSyntaxElement element, int depth)
    {
        var indent = new string(' ', depth * 2);

        switch (element)
        {
            case KokosToken { IsMissing: true } token:
                Console.WriteLine($"{indent}<missing {token.Kind}>");
                break;
            case KokosToken token:
                Console.WriteLine($"{indent}{token.Kind} '{token.Text}'");
                break;
            case KokosNode node:
                Console.WriteLine($"{indent}{node.GetType().Name}");
                foreach (var child in node.Children)
                    DumpNode(child, depth + 1);
                break;
        }
    }
}
