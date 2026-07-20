# Roadmap

## Reference implementation milestone

- [x] Isolate experimental FSRS-7 from the stable FSRS-6 API.
- [x] Support fractional same-day retrievability and the mixed forgetting curve.
- [x] Export chronological benchmark metrics and raw prediction JSONL.
- [x] Add hierarchical parameter fallback and a policy-neutral queue layer.
- [x] Provide a one-command optimize/evaluate/simulate example.
- [ ] Validate experimental trajectories against an upstream FSRS-7 conformance corpus when one is published.
- [ ] Request inclusion in the Open Spaced Repetition implementation and benchmark lists after conformance review.

## 0.1 preview

- Validate scheduling, previews, migration, packaging, and optimizer ergonomics.
- Expand official cross-implementation reference vectors.
- Publish prerelease packages with symbols and Source Link.

## 0.5 API candidate

- Freeze persistence contracts and optimizer input formats.
- Publish performance and allocation baselines on supported operating systems.
- Add compatibility reports for every release candidate.

## 1.0 stable

- Guarantee semantic versioning for public scheduling and persistence APIs.
- Document the supported FSRS-6 reference revision and every intentional deviation.
- Maintain an upgrade path if another FSRS generation is added as a separate scheduler.
