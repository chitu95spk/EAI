using System.Text;

namespace EasyLabel.Ai.Mcp;

internal static class Program
{
    private static int Main(string[] args)
    {
        var utf8 = new UTF8Encoding(false);
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8, 4096) { AutoFlush = true, NewLine = "\n" };
        return McpEntry.Run(args, stdin, stdout, Console.Error);
    }
}
