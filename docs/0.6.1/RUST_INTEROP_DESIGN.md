# C# + Rust interoperation (design, R10)

**Status:** design only. There is no Rust code in 0.6.1 and implementing it here is not authorized. This document pins down the contracts so that 0.7.0 can add workers without touching the Revit integration.

## 1. Division of responsibilities

| Responsibility | Where | Why |
|---|---|---|
| All Revit API access (reads, transactions, warnings, save/sync/export) | C# in the add-in | The API only exists in .NET and is only valid on the Revit thread; a second transaction engine would duplicate states and audit. |
| Leases, grants, audit, checkpoints, reconciliation | C# (Engine) | Already tested; a worker neither decides authority nor persists task state. |
| Geometry extraction (solids, meshes, link transforms) | C# on the Revit thread | Requires live objects; converted to flat buffers before leaving. |
| Pure computation on buffers: rays against meshes, spatial indexes (BVH/R-tree), clashes, one-to-one QC over thousands of elements | Rust candidate | No Revit state, parallelizable, measurable on equal terms. |
| Deciding what to write to the model | C# (recipe + engine) | The worker only returns results; never write commands. |

## 2. Contract

```csharp
public interface IComputeWorker : IDisposable
{
    WorkerInfo Info { get; }                       // name, worker version, supported contract versions
    Task<ComputeResult> RunAsync(ComputeRequest req, CancellationToken ct);
}

public sealed class ComputeRequest
{
    public int ContractVersion;                    // integer; the worker rejects versions it does not know (no "best effort")
    public string Kernel;                          // "rayos_vertical", "choques_aabb", "manifiesto_qc"…
    public string RequestId;                       // for audit and cancellation
    public ReadOnlyMemory<byte> Payload;           // flat buffers (below)
    public int MaxMs;                              // budget; when exceeded it returns "presupuesto_agotado" with the work done
    public long MaxBytes;                          // output cap
}

public sealed class ComputeResult
{
    public string Estado;                          // ok | presupuesto_agotado | cancelado | entrada_invalida | error
    public ReadOnlyMemory<byte> Output;
    public string Error;
    public long Ms, BytesIn, BytesOut;
}
```

## 3. Transferable data

- **Values only**, never Revit objects: `ElementId` as `long`, `UniqueId` as UTF-8 text, coordinates as `double`.
- **Units:** Revit internal feet (the same as the API) throughout the contract; conversion to mm happens only in presentation. A `units = "ft"` field in the header avoids ambiguity.
- **Coordinates:** of the host document. Link coordinates are transformed in C# before sending (the transform is tested in `Xform`).
- **Stable identity:** each row carries a `UniqueId` (or its index in a string table sent once). The result is returned by index; C# maps it back.
- **Format:** fixed header (magic, contract_version, kernel, counts) + contiguous little-endian blocks (`f64` vertices, `u32` triangles, `u32` per-element offsets). No JSON on the hot path; JSON only for the error response.

## 4. IPC, failures and cancellation

- **Separate process** (`bimbridge-compute.exe`), launched by the add-in on demand. A Rust panic does not bring down Revit.
- **Transport:** per-user named pipe with the same ACL and the same 16 MB frame as the bridge (`Frame`); the client verifies the server PID, as in `PipeServerCheck`.
- **Cancellation:** a `cancel <RequestId>` message over the same pipe; the worker checks between blocks. The `MaxMs` budget is also enforced on the C# side (timeout + kill the process).
- **Worker failure** → the recipe uses the reference C# implementation (always present). The result states which engine computed it.
- **Audit:** each request records `kernel`, `RequestId`, sha256 of input and output (not the data).

## 5. Costs and adoption criterion

- Estimated serialization: a 1,000-triangle mesh is about 36 KB (shared f64 vertices + u32 indices). 10,000 elements with about 200 triangles each are about 70 MB, above the frame cap: it will be split into element batches.
- Fixed cost per request (launching the process: once; pipe round trip: <1 ms locally, measured for the bridge) versus the computation saved.
- **Starting measurement (0.3.0, without Revit):** C# mounting for 1,442 lighting fixtures × 9 points takes 5–15 ms. At that size Rust adds nothing. The real candidates are full-model clashes and QC over tens of thousands of elements.

## 6. Comparative benchmark plan

1. Same kernel in C# (current) and Rust, same input buffers saved to disk from a practice copy.
2. Three sizes: 1,000 / 10,000 / 100,000 elements. One cold run and 10 warm; p50/p95, peak memory and serialized bytes.
3. Acceptance: identical results (same set of pairs/indices) and total p50 (serialize + IPC + compute) ≥30 % better than C# at the target size. Otherwise, Rust does not go in.
4. Measuring extraction time on the Revit thread (which Rust cannot speed up) is mandatory before step 1: if it dominates, extraction is optimized, not computation.

## Addendum 0.7.0-alpha1 — concrete candidate
Kernel identified: `TriMesh.Extent` (vertical point→mesh ray for mounting). C# bench in `src/Bimbridge.AgentHost/Kernels/MountKernelBench.cs`, command `agenthost banco-kernel`. Baseline and decision in `docs/0.7.0/RENDIMIENTO_0.7.0.md`: ~1.2 µs/point with SIMD; Rust not justified until measured in real Revit. Deterministic binary input format and sha256 digest to compare a future implementation byte for byte.
