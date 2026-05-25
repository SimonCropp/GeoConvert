using System.Diagnostics.CodeAnalysis;
using GeoConvert.Cli;

[ExcludeFromCodeCoverage(Justification = "Process entry point; logic lives in Runner.")]
static class Program
{
    static int Main(string[] args) =>
        Runner.Run(args, Console.Out, Console.Error);
}
