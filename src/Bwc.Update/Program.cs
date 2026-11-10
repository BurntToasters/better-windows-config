namespace Bwc.Update;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args is ["--help"])
        {
            Console.WriteLine("Usage: BwcUpdate.exe [--help]");
            Console.WriteLine("Updates Better Windows Config. Not implemented yet.");
            return 0;
        }

        Console.Error.WriteLine("not implemented");
        return 2;
    }
}
