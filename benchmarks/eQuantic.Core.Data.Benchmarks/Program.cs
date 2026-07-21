using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(eQuantic.Core.Data.Benchmarks.FilterTranslationBenchmarks).Assembly).Run(args);
