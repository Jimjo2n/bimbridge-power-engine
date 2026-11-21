# Known limitations of beta1

1. **Native NRT-05 has not run yet.** Everything in beta1 is tested only against a fake add-in. No success is declared until it runs on the authorized test instance.
2. **The "reopen" is an on-disk verification, not a close and reopen of the active document.** The Revit API cannot close the active document, and the procedure uses an isolated test instance. What it does is open a copy of the just-saved file as another Document. This proves what is on disk, but not a close-and-open cycle through the UI. That cycle requires a manual operation followed by `verificar_disco`.
3. **verificar_disco on large models.** Opening the copy costs the same as opening the model. With the Dynamo sample model it is acceptable; it must not be used with corporate models (which are also out of scope).
4. **Duplicate check in NRT-05.** The same journal is resumed and commits are counted. Resending the same key to the add-in with different arguments (conflict) is covered by the add-in journal tests, not by the native harness.
5. **The read-only installer fix** was tested on Linux. There the ReadOnly attribute barely applies, so the real validation of `CopyOver` is the next update on Windows.
6. **Performance in Revit** (commit, save and verificar_disco latencies) only exists once native NRT-05 runs; see PERFORMANCE_beta1.md.
7. The alpha3 limitations still apply: rev counts rolled-back transactions, the trial requires IsModified=false and native NRT-03 is pending (beta2).
