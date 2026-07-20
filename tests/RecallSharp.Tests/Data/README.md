# Official FSRS-6 vectors

`ts-fsrs-5.4.1-vectors.json` contains 2,048 deterministic state transitions generated with
`open-spaced-repetition/ts-fsrs` 5.4.1, which identifies its algorithm as FSRS-6.0.

Inputs span stability from 0.05 to 36,500 days, difficulty from 1 to 10, all four ratings, same-day reviews, and elapsed
intervals up to 729 days. The committed values allow RecallSharp CI to perform cross-implementation differential testing
without adding a JavaScript runtime dependency to either NuGet package.
