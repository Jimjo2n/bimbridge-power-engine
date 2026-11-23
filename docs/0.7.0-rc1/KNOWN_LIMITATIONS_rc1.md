# rc1 limitations and risks

1. **Nothing in rc1 ran in Revit.** NRT-03, NRT-06 and the NRT-05 regression are `BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION`. Passing in the simulator or FakeBim is not native evidence.
2. **Reopen:** it is still a copy opened as another Document, not a close and open of the active document (D17). Validating close and reopen from the UI requires an additional manual step in the campaign.
3. **Add-in chained audit:** the persistent commit is recorded in the add-in's operation journal and in the AgentHost's, but not in `auditoria.jsonl` (which records exec). Finding from the beta1.1 review, not fixed in rc1: touching the add-in chain requires native validation. Proposed for the campaign.
4. **Batches:** text parameters only. Values with units per batch remain outside rc1.
5. **Lighting fixture and tag QC:** reuses `check lighting_audit` (one tag per fixture rule). On large models, the cost is that of the full level audit.
6. **Task engine recipes** (tagging, export, qc_manifiesto): they exist in the add-in, but the AgentHost does not run them in rc1 (see README).
7. **Read-only installer:** tested with synthetic files (`CopyOver`); on real Windows it was only exercised indirectly, because the beta1 and beta1.1 installs exited with 0.
8. **Native gate:** enforced by the AgentHost. The add-in, for its part, still answers any MCP client authorized by its level configuration (that is why operator can work with AI in their Revit). The gate protects the harnesses; it does not replace the levels.
9. **Concurrency on the same document:** the plan lease and the add-in serialization (one ExternalEvent) are tested in the simulator and with real processes (AH-04). Two AgentHosts with different plans on the same real document have not been tested natively.
