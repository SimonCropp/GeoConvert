using BenchmarkDotNet.Running;

// Run all benchmarks, or filter, e.g.: dotnet run -c Release --project src/Benchmarks -- --filter *Convert*
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
