using BenchmarkDotNet.Running;

namespace Pagewing.Benchmarks;

/// <summary>Entry point: <c>dotnet run -c Release --project bench/Pagewing.Benchmarks -- --filter "*"</c>.</summary>
internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
