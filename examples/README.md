# Source-reviewed compact-profile examples

These examples illustrate the existing compact command surface. They were reviewed against source; they are not a claim that this repository preparation connected to Revit.

Start with a disposable, authorized model and inspect the selected document and client profile. Example commands for the compact read tool:

```text
ctx
q LF n=true
q LF f=id,type digest=true
```

The count avoids returning an inventory. The digest represents the complete filtered result rather than transferring all rows. A digest and a row inventory have different outputs and must not be used as equivalent performance workloads.

Write examples require explicit document identity, scope, proposed diff, conflict checks and postcondition evidence. See the existing [recipes](../docs/RECETAS.md) and [trust model](../docs/security/TRUST_MODEL.md) before preparing a typed plan or executing C#.
