# Performance methodology and baseline results

## Scope and provenance

The verified a291cc6 delivery includes a local-tool A/B report and raw measurements. This is **reported native level-B evidence**, not an independently rerun benchmark, not a client-token measurement and not an end-to-end AI workflow comparison.

The report names Revit 2024.3. It used three read repetitions and seven write repetitions, with comparable selected elements. Writes were applied, checked and reverted in trial. The raw model context, element identifiers, response dumps and scripts with local project paths remain private.

## Selected reported medians

| Workload | BIMBRIDGE ms | pyRevit ms | Ratio pyRevit / BIMBRIDGE |
|---|---:|---:|---:|
| Count | 149 | 153 | 1.03 |
| Grouped read | 228 | 433 | 1.90 |
| Complete paginated comments | 19727 | 3232 | 0.164 |
| Typed set parameter | 586 | 615 | 1.05 |
| Dynamic C# parameter trial | 118 | 615 | 5.21 |
| Move | 668 | 880 | 1.32 |
| Rotate | 1144 | 1826 | 1.60 |
| Copy | 8851 | 13543 | 1.53 |
| Five-operation single-trip batch | 4807 | 6340 | 1.32 |

Ratios above one favor BIMBRIDGE. The complete paginated read is approximately 6.1 times slower. The dynamic-code p95 reported 4724 ms because it includes first compilation, compared with a 118 ms median after reuse.

Digest comparison omits complete row transfer and must not be advertised as equivalent to reading every row. Estimated tokens use characters/4; they are not billed or measured client tokens.

## Limitations and discrepancies

Verification was asymmetric: the report says BIMBRIDGE reread after rollback while some comparison routes verified earlier or less thoroughly. Three reads and seven writes are a small sample. Background workload, caches, client transport and pagination affect comparability.

The coordinator summary and the detailed table disagree on the view-creation ratio. Keep that result unaccepted until arithmetic and raw records are reconciled. A one-operation batch serialization problem affected the harness; the five-operation batch is a distinct case.

No general 3–5× improvement is established. The long-operation session timeout and paginated traversal regression remain open defects.

## Reproduction requirements

Use a public or synthetic fixture, fixed application/add-in versions, identical selected elements, units, fields, result shape and postconditions. Separate cold and warm runs; alternate ordering; record repetitions, median, p95, errors, calls and evidence of restoration. Compare equivalent work and preserve failures.

Use [the benchmark record template](../../benchmarks/README.md). Publish only sanitized aggregate results after reviewing the raw evidence.
