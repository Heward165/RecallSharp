# RecallSharp

[![CI](https://github.com/Heward165/RecallSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Heward165/RecallSharp/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
![C# 14](https://img.shields.io/badge/C%23-14-239120)
![FSRS](https://img.shields.io/badge/FSRS-6-blue)
![Dependencies](https://img.shields.io/badge/dependencies-none-success)

A dependency-free C# 14 implementation of **FSRS-6** with an auditable optimizer, deck-scale workload simulator,
retention-policy search, versioned persistence contracts, and an isolated experimental **FSRS-7** assembly.

This repository is the scheduling algorithm only. It contains no user interface, database, flashcard editor, review queue, or application framework. Give it a card's memory state, a rating, and a timestamp; it returns the updated state and next review interval.

## Features

- Published 21-parameter FSRS-6 model
- Difficulty, Stability, and Retrievability (DSR) calculations
- Again, Hard, Good, and Easy rating transitions
- Initial learning and post-lapse relearning steps
- Dedicated FSRS-6 same-day stability calculation
- Configurable desired retention and maximum interval
- Immutable input/output records
- Deterministic and safe to reuse across threads
- Defensive copies of all parameter and step collections
- No runtime dependencies
- XML documentation and inline explanations of the equations
- Numerical conformance checks against official FSRS-6 behavior
- Four-rating schedule previews for review interfaces
- SM-2 migration using the official fsrs-rs conversion equation
- Allocation-conscious `Span<T>` batch retrievability
- Portable review-log CSV import/export
- Chronological card-level training and validation split
- Log-loss, Brier score, RMSE, and calibration diagnostics
- Deterministic coordinate-descent parameter fitting with bootstrap intervals
- Multi-year workload simulation and optimal-retention grid search
- Strict, versioned JSON persistence envelope
- .NET 8 and .NET 10 runtime compatibility
- Experimental 35-parameter FSRS-7 mixed forgetting curve with fractional intervals
- Official SRS Benchmark-style time splits, metrics, and raw JSONL prediction output
- Application-to-user-to-deck-to-category parameter fallback
- Post-model workload, weekday, sibling, and interval-growth policy rules
- One-command optimization, evaluation, and workload reference pipeline

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building
- C# 14

The packages target both .NET 8 and .NET 10.

## Getting started

Clone or download the repository, then build the solution from its root:

```bash
git clone https://github.com/Heward165/RecallSharp.git
cd RecallSharp
dotnet build RecallSharp.slnx -c Release
```

Until the library is published as a package, reference the project directly from another .NET project:

```bash
dotnet add YourProject.csproj reference src/RecallSharp/RecallSharp.csproj
```

The repository contains four independently usable assemblies:

| Assembly | Purpose |
| --- | --- |
| `RecallSharp` | deterministic FSRS-6 transitions, previews, batching, and persistence |
| `RecallSharp.Optimizer` | review-log interoperability, evaluation, fitting, and uncertainty |
| `RecallSharp.Analytics` | deck simulation, workload distributions, and retention selection |
| `RecallSharp.Fsrs7.Experimental` | isolated fractional-time FSRS-7 research implementation |

## Basic usage

```csharp
using RecallSharp;

var scheduler = new FsrsScheduler();
var now = DateTimeOffset.UtcNow;

// New cards are immediately due and have no DSR values yet.
var card = FsrsMemoryState.New(
    cardId: 42,
    createdAt: now);

// Apply the learner's final rating.
FsrsReviewResult result = scheduler.Review(
    state: card,
    rating: FsrsRating.Good,
    reviewedAt: now);

// Persist this state in your own storage system.
FsrsMemoryState updatedCard = result.Current;

DateTimeOffset nextReview = updatedCard.DueAt;
TimeSpan interval = result.Interval;
double stability = updatedCard.Stability!.Value;
double difficulty = updatedCard.Difficulty!.Value;
```

The scheduler does not read the system clock or modify the supplied state. Always pass the actual review timestamp and save `result.Current` after a successful transaction in your application.

## Preview every rating

Review interfaces can calculate all four candidate intervals before the learner chooses one:

```csharp
FsrsSchedulingPreview choices = scheduler.Preview(card, now);

TimeSpan againInterval = choices.Again.Interval;
TimeSpan hardInterval = choices.Hard.Interval;
TimeSpan goodInterval = choices.Good.Interval;
TimeSpan easyInterval = choices.Easy.Interval;
```

Previewing is pure: every result starts from the same input state and the supplied state is unchanged.

## Migrate an existing scheduler

When complete review history is unavailable, approximate an FSRS state from an SM-2-style ease and interval:

```csharp
FsrsMemoryState migrated = scheduler.MigrateFromSm2(
    cardId: 42,
    easeFactor: 2.5,
    intervalDays: 10,
    assumedRetention: 0.9,
    lastReviewAt: lastReview);
```

Replaying complete history remains preferable because migration necessarily estimates hidden memory state.

## Train and validate parameters

The optimizer consumes complete card histories. It holds out the newest card histories, so validation never reuses a
card seen during fitting.

```csharp
using RecallSharp.Optimization;

IReadOnlyList<FsrsReviewLog> logs = FsrsReviewLogCsv.Import(csv);
IReadOnlyList<FsrsCardHistory> histories = FsrsCardHistory.Group(logs);

FsrsOptimizationResult fit = FsrsParameterOptimizer.Optimize(
    histories,
    new FsrsOptimizationOptions
    {
        MaximumPasses = 12,
        BootstrapSamples = 100,
        BootstrapSeed = 42
    });

double heldOutLogLoss = fit.ValidationMetrics.LogLoss;
IReadOnlyList<double> trainedParameters = fit.Parameters;
```

The implementation is deliberately transparent and dependency-free. It is suitable for reproducible local fitting and
portfolio inspection, but it does not claim the throughput of tensor-based optimizers. Always compare held-out metrics
against the default parameters before adopting trained weights.

## Simulate workload and choose retention

```csharp
using RecallSharp.Analytics;

var definition = new DeckSimulationDefinition
{
    Days = 730,
    DeckSize = 5_000,
    NewCardsPerDay = 20,
    MaximumReviewsPerDay = 500,
    Seed = 42
};

OptimalRetentionReport policy = OptimalRetentionCalculator.Evaluate(
    definition,
    candidateRetentions: [0.80, 0.85, 0.90, 0.92, 0.95],
    objective: new RetentionObjective(
        StudyMinuteCost: 0.25,
        RememberedCardValue: 1,
        MaximumPeakDailyReviews: 450),
    parameters: fit.Parameters);
```

The result contains every candidate simulation, not only the winner. Workload limits and utility weights are explicit;
the library never presents one retention target as universally optimal.

## Experimental FSRS-7

FSRS-6 remains RecallSharp's stable scheduler. FSRS-7 lives in a separate prerelease assembly so adopting its evolving
35-parameter model cannot silently change existing schedules:

```csharp
using RecallSharp.Fsrs7.Experimental;

var scheduler7 = new Fsrs7Scheduler();
FsrsReviewResult reviewed = scheduler7.Review(card, FsrsRating.Good, now);
double afterOneHour = scheduler7.Retrievability(reviewed.Current, now.AddHours(1));
```

The implementation follows the current Open Spaced Repetition benchmark model: fractional elapsed days, two weighted
power-law forgetting components, distinct short- and long-term transitions, and numerical interval inversion. Treat the
API and defaults as experimental until upstream FSRS-7 stabilizes.

## Hierarchical fitting and scheduling policy

`FsrsParameterHierarchy.Resolve` selects the narrowest parameter set with enough predictive reviews, falling back from
category to deck, user, and application defaults with an auditable reason list. `FsrsSchedulingPolicy.Apply` runs after
the memory model and returns a queue due date without rewriting predicted stability or retrievability. It can bound
same-day intervals and interval growth, prefer weekdays, balance projected workload, and separate sibling cards.

## Reference benchmark command

The reference command consumes `user_id,card_id,reviewed_at,rating` CSV, performs chronological held-out evaluation,
compares FSRS-6, experimental FSRS-7, and a training-set baseline, exports official raw prediction JSONL (`user`, `p`,
`y`), fits FSRS-6 parameters, and simulates retention/workload candidates:

```powershell
dotnet run --project tools/RecallSharp.Reference -c Release -- `
  reviews.csv artifacts/reference-run
```

The summary reports log loss, RMSE, benchmark-style binned RMSE, AUC, calibration error, optimization diagnostics, and
the complete workload search. The adapter deliberately uses CSV so the runtime remains dependency-free; convert Anki's
SQLite or benchmark Parquet data to the documented interchange columns before running it.

## Ratings

FSRS ratings describe recall quality, not the interval the learner wants:

| Rating | Value | Meaning |
| --- | ---: | --- |
| `Again` | 1 | Forgotten, incorrect, or materially incomplete |
| `Hard` | 2 | Correct with substantial effort or hesitation |
| `Good` | 3 | Correct with ordinary effort |
| `Easy` | 4 | Correct, immediate, and unambiguous |

`Hard` is a successful recall. It must not be used when the answer was forgotten.

## Memory state

`FsrsMemoryState` contains only scheduling data:

| Property | Description |
| --- | --- |
| `CardId` | Identifier supplied by the host application |
| `Stage` | New, Learning, Review, or Relearning |
| `Step` | Current zero-based short learning step |
| `Stability` | Days until predicted recall falls to 90% |
| `Difficulty` | Estimated item difficulty from 1 to 10 |
| `DueAt` | Next due timestamp |
| `LastReviewAt` | Previous review timestamp |
| `Repetitions` | Total completed reviews |
| `Lapses` | Times a graduated Review card was rated Again |

Prompts, answers, tags, decks, and review history belong to the host application and are intentionally excluded.

## Retrievability

Retrievability is the estimated probability of recalling a card at a particular time:

```csharp
double probability = scheduler.Retrievability(
    state: updatedCard,
    at: DateTimeOffset.UtcNow);
```

FSRS-6 uses the trainable forgetting curve:

```text
R(t, S) = (1 + factor * t / S) ^ -w[20]
```

The factor is chosen so that `R(S, S) = 0.90`. In other words, stability is the interval at which predicted recall reaches 90%.

## Configuration

The defaults are:

| Setting | Default |
| --- | ---: |
| FSRS version | 6 |
| Desired retention | 90% |
| Learning steps | 10 minutes |
| Relearning steps | 10 minutes |
| Maximum interval | 36,500 days |
| Parameters | Published FSRS-6 21-weight defaults |

Create a modified configuration with a record expression:

```csharp
var options = FsrsOptions.Default with
{
    DesiredRetention = 0.92,
    MaximumIntervalDays = 3_650,
    LearningSteps =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(20)
    ],
    RelearningSteps = [TimeSpan.FromMinutes(10)]
};

var scheduler = new FsrsScheduler(options);
```

Desired retention must be between 0 and 1. Raising it produces shorter intervals and more reviews. Do not manually tune the 21 model weights; replace them only with parameters produced by a compatible FSRS-6 optimizer.

## Learning transitions

The default configuration uses one 10-minute learning step:

| Current stage | Rating | Result |
| --- | --- | --- |
| New/Learning | Again | Restart the first learning step |
| New/Learning | Hard | Repeat the current step with a longer delay |
| New/Learning | Good | Advance or graduate to Review |
| New/Learning | Easy | Graduate immediately to Review |
| Review | Again | Increment lapses and enter Relearning |
| Review | Hard/Good/Easy | Remain in Review with a new FSRS interval |
| Relearning | Good/Easy | Graduate back to Review |

Set `LearningSteps` or `RelearningSteps` to an empty collection to disable the corresponding short-step sequence.

## Persistence

Persistence is intentionally outside the library. A host application should store at least the complete `FsrsMemoryState` after every review.

For auditable history, also store:

- the previous state;
- rating and review timestamp;
- `PredictedRetrievability`;
- the resulting state and interval;
- the FSRS version and parameter set in use.

Update the memory state and insert the review event in one database transaction.

`RecallSharpDocument` is an optional self-describing envelope for applications that want a portable checkpoint:

```csharp
RecallSharpDocument document = RecallSharpDocument.Create(scheduler, updatedCard, reviewEvents);
string json = RecallSharpJson.Serialize(document);
RecallSharpDocument restored = RecallSharpJson.Deserialize(json);
```

Deserialization rejects unknown schema fields, unsupported schema versions, incompatible scheduler versions, malformed
parameters, and invalid prediction values.

## Verification

Run the dependency-free conformance executable:

```bash
dotnet run --project tests/RecallSharp.Conformance -c Release
```

The checks cover:

- the 90% stability invariant;
- all four initial ratings;
- official retrievability and stability reference vectors;
- three multi-review differential trajectories generated with py-fsrs 6.3.0;
- Hard and Easy behavior;
- lapses and relearning;
- same-day FSRS-6 updates;
- maximum interval enforcement;
- invalid state rejection;
- defensive parameter isolation.
- previews and span-based batch calculations;
- strict JSON round-tripping;
- review-log CSV interoperability;
- evaluation and held-out optimization;
- deck simulation and retention selection.

Expected output:

```text
RecallSharp scheduling, persistence, optimization, and analytics checks passed.
```

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the local
verification commands, pull request scope, and the information needed to report
a reproducible scheduling defect.

## Project structure

```text
src/
  RecallSharp/
    FsrsModels.cs       Public records, enums, and options
    FsrsScheduler.cs    FSRS-6 equations and transitions
    RecallSharpPersistence.cs
    RecallSharpPolicy.cs
  RecallSharp.Experimental/
    Fsrs7Options.cs            Versioned 35-parameter defaults
    Fsrs7Scheduler.cs          Fractional-time experimental model
  RecallSharp.Optimizer/
    ReviewLogs.cs               Portable histories and CSV
    FsrsModelEvaluator.cs       Prediction and calibration metrics
    FsrsParameterOptimizer.cs   Direct deterministic fitting
    FsrsHoldoutOptimizer.cs     Held-out fitting and uncertainty
  RecallSharp.Analytics/
    DeckSimulator.cs            Seeded workload simulation
    OptimalRetention.cs         Explicit policy grid search

tests/
  RecallSharp.Conformance/
    Program.cs          Dependency-free conformance checks
  RecallSharp.Tests/    Unit, migration, optimizer, and randomized invariant tests

examples/
  RecallSharp.Example/  Rating previews and SM-2 migration

benchmarks/
  RecallSharp.Benchmarks/  Throughput and allocation measurement

tools/
  RecallSharp.Reference/   End-to-end benchmark and workload pipeline
```

See [architecture](docs/architecture.md), [benchmarks](docs/benchmarks.md), and the [release roadmap](docs/roadmap.md).

## Scope

This library schedules individual memory states. A complete learning application still needs to provide:

- notes, cards, prompts, and answers;
- durable storage and review-event history;
- due-card queue ordering;
- sibling burying and new-card limits;
- user-specific empirical behavior beyond the documented simulation assumptions;
- user interaction and answer grading.

Keeping those concerns outside the scheduler makes the algorithm easy to test, embed, and replace.

## Releases and compatibility

Packages contain symbols, Source Link metadata, deterministic builds, and package validation. Tagged prereleases create a
GitHub Release and publish to NuGet when the repository's `NUGET_API_KEY` secret is configured. Public API changes follow
semantic versioning after 1.0.

RecallSharp is licensed under the [MIT License](LICENSE). See [SECURITY.md](SECURITY.md) for private vulnerability reports
and [CHANGELOG.md](CHANGELOG.md) for release history.

## References

- [The FSRS algorithm](https://github.com/open-spaced-repetition/awesome-fsrs/wiki/The-Algorithm)
- [ABC of FSRS](https://github.com/open-spaced-repetition/fsrs4anki/wiki/ABC-of-FSRS)
- [Open Spaced Repetition](https://github.com/open-spaced-repetition)
- [fsrs-rs optimizer and simulator](https://github.com/open-spaced-repetition/fsrs-rs)
- [SRS benchmark](https://github.com/open-spaced-repetition/srs-benchmark)
