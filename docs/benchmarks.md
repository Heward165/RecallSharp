# Benchmarks

Run the dependency-free benchmark in Release mode:

```bash
dotnet run --project benchmarks/RecallSharp.Benchmarks -c Release
```

The report records reviews per second, managed allocations per review, runtime version, and operating system. Results are
environment-specific and should only be compared when the command, runtime, hardware, and parameter set are unchanged.

The benchmark deliberately avoids a benchmark framework so no package becomes part of the runtime library. CI runs it as
a smoke test; release notes may record measured baselines from dedicated hardware.

## Development baseline

The 2026-07-17 development run on .NET 10.0.7 and Windows completed 250,000 mixed Good/Again transitions at approximately
2.01 million reviews per second with 192 managed bytes allocated per review. This is a regression baseline from one machine,
not a cross-hardware performance claim.
