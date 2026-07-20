# Contributing to RecallSharp

Thanks for helping improve RecallSharp. Changes should keep the library small,
dependency-free, deterministic, and focused on FSRS-6 scheduling.

## Before opening a pull request

1. Create a focused branch from `main`.
2. Keep public API changes backward-compatible when possible.
3. Add or update conformance checks for behavior changes.
4. Run the same checks used by CI:

   ```bash
   dotnet restore RecallSharp.slnx
   dotnet format RecallSharp.slnx --no-restore --verify-no-changes
   dotnet build RecallSharp.slnx -c Release --no-restore
   dotnet run --project tests/RecallSharp.Conformance -c Release --no-build
   dotnet pack src/RecallSharp/RecallSharp.csproj -c Release --no-build -o artifacts/packages
   ```

## Reporting scheduling defects

Include enough information to reproduce the transition:

- the complete `FsrsMemoryState` before review;
- the rating and review timestamp;
- any non-default `FsrsOptions` values;
- the actual result and the expected result;
- a reference vector or FSRS source when the expected value comes from another implementation.

Avoid including private flashcard content. Scheduling defects can be reproduced
from state, configuration, rating, and timestamps alone.

## Pull request scope

Prefer one behavioral change or documentation topic per pull request. Explain why
the change belongs in the scheduling library rather than in a host application,
and call out any numerical tolerance or compatibility tradeoff.
