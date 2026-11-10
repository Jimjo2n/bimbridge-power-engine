# Native workflow acceptance: Copy/Monitor plumbing fixtures

> **Status:** historical partial native evidence + future integration acceptance plan. **NOT** a passing workflow, native validation of the current PR, or permission to operate a user's Revit model.

## Evidence provenance

- **Test date:** 2026-11-10.
- **Installed build actually tested:** BIMBRIDGE `0.9.0+a291cc6c72f8c47efde64db3d29fef6bb2a156ec`.
- **Environment:** Autodesk Revit **2026.4**, .NET 8; an editable local host project and a loaded architectural plumbing RVT link.
- **Candidate at handoff:** PR [#22](https://github.com/Jimjo2n/bimbridge-power-engine/pull/22), initially `3e09581c00c2aa600e4521293c9eb2d3c635ded3`. **This candidate was not executed during the native assessment.**
- **Evidence category:** user-supplied native test handoff; original responses are in a separate private conversation, not embedded here. Figures are historical, not reproduced.
- **Privacy:** the host/linked RVT and template belong to a separate academic task. Their paths, filenames, element IDs, screenshots and raw journals are intentionally omitted. **Never commit those files or reproduce their contents as fixtures.**

## What was observed

| Stage | Historical result | Confidence |
|---|---|---|
| MCP Bridge handshake, tool listing, target Revit/document resolution | Responded | Observed |
| RVT link loaded and its transform | Identity transform; model/reference aligned | Verified as a geometric state; *not* proof of insertion method |
| Plumbing Fixtures inventory in linked model | 88 source instances across 5 type labels (40 / 2 / 20 / 20 / 6) | Observed |
| Plumbing Fixtures inventory in host | 0 before operation | Observed |
| Typed `guide escritura op=copy_monitor` | Operation absent | Observed in **a291cc6**, re-test on future build |
| Dynamic C# + `PostableCommand.CopyMonitorSelectLink` | `PostCommand` accepted; `posted=true`, `cambios=ninguno` | **Command submission only** |
| Native UI mode active and expected link selected | No verified visual evidence | NOT_TESTED |
| Coordination Settings, type mapping, Batch Copy, Fixtures Found dialog, Finish | Not performed or verified | NOT_TESTED |
| Host instances created + native monitoring relationships | Not verified | NOT_TESTED |
| Saved/reopened final host document | Not verified | NOT_TESTED |

**Invariant: `PostCommand accepted` ≠ `native Copy/Monitor completed` ≠ `saved and independently verified`.** A plain `CopyElements` result without native monitoring does not meet this workflow's semantic contract.

## What Autodesk documents

- Revit Copy/Monitor handles defined element classes and MEP fixtures, not arbitrary geometry; type/instance attributes may change. See [2026 supported categories](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-Collaborate/files/GUID-46735245-3836-4470-99C0-320397718BE6.htm).
- The fixture process uses **Select Link → Coordination Settings → Batch Copy → Fixtures Found → Finish**, with applicable mapping/hosting requirements. See [Copy Fixtures for Monitoring](https://help.autodesk.com/cloudhelp/2022/ENU/Revit-Collaborate/files/GUID-8314A5A3-2FBB-425B-9500-A4A7747F640A.htm).
- Settings include **Allow batch copy**, **Copy individually**, **Ignore category**, **Copy original type**, **Specify type mapping** and **Don't copy this type**. See [Coordination Settings](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-Collaborate/files/GUID-F1F4F05D-817C-48AA-A13C-C07C820C41DA.htm) for behavior.
- `PostableCommand.CopyMonitorSelectLink` is an available Revit command. This **does not establish** that the public API exposes all subsequent interactive steps. See [Revit 2026 PostableCommand](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/f6ccdc1b-6ac3-9c49-d0bb-8a7d1877eab0.htm).
- `Element.GetMonitoredLinkElementIds()` returns *link instance IDs*, not a guaranteed per-source-fixture mapping. Verification must not invent source IDs from these results.

## Implementation contracts

### A. Truthful capability reporting and receipts — issue [#23](https://github.com/Jimjo2n/bimbridge-power-engine/issues/23)

Discover support separately for `read/preflight`, `typed plan`, `direct API`, `native interactive invocation`, `UI-assisted`, and `manual handoff`, across Revit builds and available agent/UI tools. State each as proven/experimental/unavailable and include unresolved prerequisites.

Return structured, auditable workflow states: preflight, command accepted, mode observed, link selected, mapping configured, copying, awaiting Finish, Finish observed, verification passed, saved, unknown/reconciliation required. **Only report a state after its supporting evidence exists.**

Tie every attempt to an agent, request/trace/job, Revit build, document, link instance, mapping/configuration fingerprint and idempotency key. On timeout, disconnect, modal dialog, process termination or uncertain completion: inspect/reconcile before any retry.

### B. Typed workflow orchestration and independent verification — issue [#24](https://github.com/Jimjo2n/bimbridge-power-engine/issues/24)

Investigate actual **public** Revit API surfaces for Copy/Monitor on all supported versions. Segment API-supported steps, authorized UI-assisted steps and manual-only steps. Do not promise headless automation unless demonstrably supported. No private APIs, UI coordinate guesses or ordinary `CopyElements` presented as native monitoring.

Record pre/post inventories with source and host element identifiers, category, family *ID+name*, type *ID+name*, level, location, orientation, host requirements, link transform and mapping rule. Native monitoring checks must demonstrate relationships without over-interpreting link instance IDs. Explain unobservable per-fixture relationships.

Classify each fixture as copied+monitored, justified excluded, missing, duplicate, mismapped, unmonitored, or unverifiable. Never use only overall counts to declare success.

### C. MCP and target diagnostics — issue [#25](https://github.com/Jimjo2n/bimbridge-power-engine/issues/25)

Distinguish server/stdio Bridge health, client registration and exposure of tools, actual available desktop UI executor, Revit PID/build, target document, current session and stale ephemeral document handle. In multi-Revit environments, reject ambiguous or changed write destinations instead of relying on window focus.

## Reproducible synthetic native acceptance fixture

**Do not use the historical user's project.** Construct a licensed-to-distribute, synthetic linked architectural RVT and a disposable host MEP project (optionally from a permitted public template), with:

- Exactly **88** source plumbing fixtures across five deliberately distinguishable **family+type** identities with counts **40 / 2 / 20 / 20 / 6**.
- Identity link transform for baseline, plus a separate rotated/translated link fixture.
- Configurable copy behavior, type mapping (original/substitute/exclude), hosting compatibility, levels and known positions.
- Seeded cases for one missing, one duplicated, one unmapped and one unmonitored destination element.

### Native acceptance matrix

| Test | Required evidence |
|---|---|
| Preflight | Revit build, document, link, categories, transform, 88-item source inventory, host baseline |
| Execution feasibility | Public API/direct/interactive/manual capability accurately identified per client/build |
| Mapping | Original, substituted, excluded and incompatible type cases report explicit outcomes |
| Actual Batch Copy | Supported method observed through Batch Copy, Fixtures Found and Finish; **not** merely posted |
| Copies | Expected host inventory equals eligible count minus itemized exclusions; correct family/type, locations, rotations and hosting |
| Monitoring | Evidence of native monitor relationships for eligible expected elements, with explicit API limits |
| Repeat invocation | No unintended new copies; compare source/host relationship state |
| Interruptions | Before/after Batch Copy; before Finish; reconnect, late response, modal busy, restart; no blind repeat or false success |
| Multi-instance | Wrong Revit build, document switch and stale session handle rejected |
| Save/reopen | Confirmed only in a separately authorized disposable native test environment |
| Measurement | End-to-end wall time, UI wait, engine/API time, MCP calls/bytes, reattempts and verification coverage |

Native tests must identify **source commit**, **installed build**, **Revit version**, **synthetic fixture checksum**, **client/UI capabilities**, **evidence location** and **reviewer**. Compare to manual native Copy/Monitor for functional parity; pyRevit or a normal geometry-copy benchmark is not an equivalent reference.

## Merge/release gates

- This case does **not** retroactively invalidate the offline fixes in PR #22; it creates an explicit native-workflow gap to address in a subsequent consolidated development iteration.
- No conclusion about the PR #22 candidate's Copy/Monitor capability is valid until retested on that specific build.
- `0.9.0+commit` continues. No release or **native Copy/Monitor PASS** from the historical PostCommand-only evidence.
- Production, academic, cloud/workshared Revit documents, installs, syncs, saves or uploads require fresh explicit authorization; this document **does not** grant any.

## Source observations and issue tracker

- Historical native handoff delivered to BIMBRIDGE DEV COORDINADOR on 2026-11-10; private raw evidence retained outside the public-facing repo.
- Implementation: [#23](https://github.com/Jimjo2n/bimbridge-power-engine/issues/23), [#24](https://github.com/Jimjo2n/bimbridge-power-engine/issues/24), [#25](https://github.com/Jimjo2n/bimbridge-power-engine/issues/25).
- Related backlog: [#6](https://github.com/Jimjo2n/bimbridge-power-engine/issues/6) native acceptance; [#8](https://github.com/Jimjo2n/bimbridge-power-engine/issues/8) benchmark; [#5](https://github.com/Jimjo2n/bimbridge-power-engine/issues/5) status UI.
