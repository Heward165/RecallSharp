# Architecture

## Version boundary

The stable `RecallSharp` assembly owns FSRS-6 state transitions and policy-neutral memory predictions.
`RecallSharp.Fsrs7.Experimental` references the stable state contracts but owns its parameters, forgetting curve,
fractional interval solver, and transition equations. This one-way dependency prevents an experimental model update from
changing FSRS-6 schedules.

Queue policy is similarly one-way: `RecallSharp.Policy` consumes a completed model result and returns a separate policy
due date. Parameter hierarchy belongs to the optimizer assembly because evidence thresholds and fitted-scope selection
are training concerns, not scheduler state.

RecallSharp separates four concerns:

1. `RecallSharp` contains immutable scheduling state and the FSRS-6 equations.
2. `RecallSharp.Optimizer` fits parameter sets without adding dependencies to the scheduler package.
3. Host applications own content, persistence, queues, and transactions.
4. Conformance, unit, example, and benchmark projects verify behavior without becoming runtime dependencies.

Algorithm versions are explicit. `Fsrs6Scheduler` identifies the implemented equations; `FsrsScheduler` remains a
compatibility name. A future algorithm generation must use a new versioned scheduler rather than silently changing
existing results.

The scheduler never reads the clock, writes storage, or mutates supplied state. This makes a transition reproducible from
the previous state, rating, timestamp, version, and parameter set.
