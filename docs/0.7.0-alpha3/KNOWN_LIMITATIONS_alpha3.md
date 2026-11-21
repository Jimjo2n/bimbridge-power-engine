# Known limitations of alpha3

1. **NRT-04 has not run in Revit yet.** Everything native in alpha3 was tested against a simulated add-in (FakeBim) that speaks the real protocol over a named pipe. Native acceptance is pending on the test machine with a disposable copy.
2. **Native NRT-03 is pending for beta2.** The simulated version (AgentHost crash at 5 points) passes. Natively, only the AgentHost may be killed, never Revit.
3. **The real catalog only covers `consultar` and `fijar_parametro`.** `fijar_parametro` only accepts `unidades=texto` and at most 30 targets, because the add-in's diff returns 30 rows. `verificar_qc` is not supported against Revit.
4. **`persistente` mode is implemented and tested only with the simulator.** It will not be used on real models until beta1, and always on local copies that are not workshared.
5. **Document identity depends on the add-in's `clave_doc` and `huella_doc`.** If two copies have the same path and the same fingerprint, they cannot be told apart. Mitigation: the harness uses the PID and checks that the document is not workshared.
6. **The grant key is still Base64 under the profile ACL.** DPAPI is left for beta1.
7. **The add-in level on the PC is RPW.** Reducing it to R when not writing (least privilege) depends on the operator's decision.
8. **The `clave.bin.lock` lock is left as an empty file in the authorizations folder.** It is harmless, because the operating system releases the lock.
9. **`exec ensayo` still runs arbitrary C# code.** It is always rolled back, but it does not replace a typed operation and is not used in NRT-04.
10. **Rolling back a TransactionGroup leaves `IsModified=false`** (observed in NRT-04 run4). P0 fix:
    - `commit modo=ensayo` and `exec modo=ensayo` are rejected in the add-in, on the Revit thread and before opening the group, if the document has `IsModified=true` (`TrialGuard`). This applies to any MCP client.
    - Temporary capture (`cap`) and task QC no longer open their temporary group if there are unsaved changes.
    - Unavoidable path: a **real** commit that fails and is rolled back on a modified document. It cannot be blocked without preventing normal work, so it is reported with `marca_cambios_sin_guardar_perdida` and a warning.
    - Still unprotected: failures of `exec ejecutar` and of task steps with a transaction.
    - It has not been verified natively whether rolling back **a standalone transaction** (not a group) also clears the flag.
    - `IsModified` is never written; a test checks this.
11. **The add-in's `rev` increases with rolled-back transactions** (it counts `DocumentChanged`; it only invalidates caches). The revised criterion is `rev_no_retrocede`. run4 remains FAILED in the history, because the criterion was changed afterwards.
