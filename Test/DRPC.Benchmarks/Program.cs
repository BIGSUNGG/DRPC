using BenchmarkDotNet.Running;

namespace DRPC.Benchmarks;

/// <summary>
/// DRPC runtime hot-path microbenchmarks — measures the hub runtime only, with no network (FakeSession).
/// Run: dotnet run -c Release --project Test/DRPC.Benchmarks [-- --filter *Roundtrip*]
/// </summary>
public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
