# Test report — 0.7.0-alpha1

Date: 2026-10-26 · Environment: Linux container, .NET 8.0, **without Revit**. Everything below is simulated; nothing is native validation.

## Result
- Full suite (rc3 + 0.7.0): **325/326**, final run `evidencia/dotnet_test_completo.log`.
- New AgentHost tests: **54/54**.
- The only failure is `Rc3Tests.Key_ConcurrentInitialisation_ProducesOneKey`, an rc3 test. **It already fails on base 7be12ad** (2 of 3 isolated runs in a clean worktree of 7be12ad). Probable cause: a race when creating GrantStore's `clave.bin` with 16 threads; one instance ends up with another key and rejects a valid grant. It fails **closed** (denies, never grants too much). Not touched per the mandate not to reopen 0.6.1; it remains a known defect.
- Earlier runs under load: `EndToEndTests.HeldCommit_WaitsForUser…` (rc3) failed once and passes in isolation; see `evidencia/dotnet_test_corrida1_flaky_e2e.log`. Three new tests were load-sensitive (600 ms lease TTL, queue race, plan too short) and were fixed in the test, not in the code.
- Full solution in Release: 0 errors.

## Section 9 → tests map
| Requirement | Test |
|---|---|
| AH-01 six crashes (mid tx, after commit, after run, during verification, after verification, during checkpoint) | AgentHostProcessTests.AH01_* (real child processes, SIGKILL) |
| AH-01 compacted effect without evidence / partial / failed verification after recovery | AH01_CompactedModel…, AH01_Partial…, AH01_FailedVerificationAfterRecovery_IsFailure_NotSuccess |
| AH-02 durable model across processes | AH02_* |
| AH-03 corrupt state | AgentHostAlphaTests.AH03_* (middle of journal, checkpoint, illegal transition, unknown op, fencing, broken/corrupt WAL) |
| AH-04 exclusive ownership / zombie | AH04_* (two processes compete, expired lease, zombie with old fencing) |
| AH-05 empty | AH05_* |
| Service | AgentHostServiceTests (5) including a killed service process recovered without duplicates |
| Documents | AgentHostDocumentTests (9) |
| 1,000 operations with failures and interruptions | AgentHostScenarioTests.Scenario1000_SevenForcedTerminations_CompletesSafely_WithFullReport |

## AgentHost tests (literal output)
```
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_CheckpointAheadOfJournal_IsRejected [19 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_CorruptedMiddleRecord_RefusesRecovery [34 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_ForgedCheckpointClaimingCompletion_IsRejected [17 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_IllegalTransition_UnknownOperation_UnknownState_AreRejected [214 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_JournalOfAnotherSchemaVersion_IsRejected [8 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_MissingJournalWithCheckpoint_IsRejected [38 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_PlanFileTampered_OrDifferentPlan_IsRejected [16 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_TamperedCheckpoint_IsDiscarded_AndRebuiltFromJournal [29 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH03_TornLastJournalLine_IsTrimmed_AndRunContinues [22 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH04_ZombieOwnerAfterTakeover_CannotWriteModelOrJournal [274 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH05_EmptyTargetSet_ExplicitlyAllowed_IsVerifiedAsEmpty_NotAsModification [26 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH05_EmptyTargetSet_WriteFailsWithDiagnostic_NothingWritten [14 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.AH05_ObserveWithZeroTargets_IsDistinctFromApplied_AndBackendRefusesDirectly [6 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.Cancellation_StopsBetweenOperations_AndCancelMarksTheRest [31 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.DocumentFingerprintMismatch_RefusesBeforeAnyExecution [4 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.IR_DependencyCyclesAreRejected_WithTheirPath [6 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.IR_IdIncludesDocumentFingerprintAndSource_AndIsKeyOrderIndependent [< 1 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.IR_OldSchemaVersion1_IsRejectedWithoutBestEffort [< 1 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.IR_RejectsAgentDeclaredSystemFields_ImplicitUnits_DuplicatesAndBadSources [1 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.IR_ValidatedPlanIsImmutable_AndSurvivesJsonRoundTrip [3 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.Model_TornWalLine_IsNotCommitted_CorruptLineRefuses [6 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.Pause_PersistsAcrossRestart_ResumeContinues [60 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.PreconditionOnTargetCount_FailsBeforeWriting [16 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.RealModelBackend_WithWrites_IsRefusedInAlpha1 [2 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.RepeatedExecutionOfACompletedPlan_DoesNothing [42 ms]
Passed Bimbridge.Tests.AgentHostAlphaTests.TransientRejections_RetriedOnlyUpToPolicy_RejectionsAreNotEffects [48 ms]
Passed Bimbridge.Tests.AgentHostDocumentTests.DocumentInstructionsCannotGrantPermissions_NorDeclareSystemFields [12 ms]
Passed Bimbridge.Tests.AgentHostDocumentTests.DuplicateRequirementIds_AreReportedAtExtraction [< 1 ms]
Passed Bimbridge.Tests.AgentHostDocumentTests.Extraction_KeepsPageSectionIdAndOriginal_WithoutInterpreting [1 ms]
Passed Bimbridge.Tests.AgentHostDocumentTests.Interpretation_ConvertsOnlyUnambiguous_AndPreservesTheRestForReview [9 ms]
Passed Bimbridge.Tests.AgentHostDocumentTests.MountKernel_IsDeterministic_AndSerializationRoundTripsExactly [87 ms]
Passed Bimbridge.Tests.AgentHostDocumentTests.PlanFromDocument_ExecutesAgainstSimulatedModel_WithTraceabilityInReport [30 ms]
Passed Bimbridge.Tests.AgentHostDocumentTests.RevitAdapter_MapsToInstalledRecipes_DeterministicallyAndRefusesToExecute [7 ms]
Passed Bimbridge.Tests.AgentHostDocumentTests.TamperedSourceDocument_IsDetected_AndChangesOperationIds [38 ms]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_CrashAfterAllElementsCommitted_RecoveredFromModelEvidence_VerifiedWithoutRepeating [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_CrashAfterCommitBeforeResultSaved_ResultReconstructed_NotRepeated [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_CrashAfterFourOfFiveElements_RolledBack_ExecutedOnceOnResume [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_CrashAfterUnexpectedNumberOfElements_VerificationFails_NotRepeated [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_CrashAfterVerificationBeforeFinalRecord_ReverifiedOnly [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_CrashDuringVerification_ReverifiedOnly [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_EffectProvenButEvidenceCompacted_RequiresVerification_NeverVerifiedNorRepeated [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_FailedVerificationAfterRecovery_IsFailure_NotSuccess [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH01_TargetsChangedAfterCommit_PartialEffect_RequiresReconciliation [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH02_ModelAndStateSurviveRealProcessDeath_CompletesWithoutDuplicates [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH03_RestartDuringCheckpointWrite_RecoversFromJournal [1 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH04_LeaseExpiresAfterCrash_NextProcessTakesOverWithHigherFencing [2 s]
Passed Bimbridge.Tests.AgentHostProcessTests.AH04_TwoProcessesCompete_OnlyOneExecutes_NoDuplicates [875 ms]
Passed Bimbridge.Tests.AgentHostScenarioTests.Measure_CompactMessageSizes_Bytes [2 s]
Passed Bimbridge.Tests.AgentHostScenarioTests.Scenario1000_SevenForcedTerminations_CompletesSafely_WithFullReport [19 s]
Passed Bimbridge.Tests.AgentHostServiceTests.Service_BoundedQueue_RejectsWhenFull [201 ms]
Passed Bimbridge.Tests.AgentHostServiceTests.Service_GracefulShutdown_ThenNewHostRecoversOnStartup [6 s]
Passed Bimbridge.Tests.AgentHostServiceTests.Service_PauseResumeCancel_ArePersistedAndRespected [1 s]
Passed Bimbridge.Tests.AgentHostServiceTests.Service_ProcessKilled_NewServiceProcessRecoversWithoutDuplicates [4 s]
Passed Bimbridge.Tests.AgentHostServiceTests.Service_SubmitRunInspectReport_Idempotent_StructuredErrors [2 s]
```

## 1,000-operation scenario — literal log
Design: 2 queries, 980 writes on 10 levels with chains of 5 dependencies, 10 QC checks, 8 writes on a non-existent level (4 with permitir_vacio). Deliberate faults: 3 rejections, 5 recoverable transients, 2 unrecoverable, 5 wrong expected results, 1 QC with 2 deviations. Seven real SIGKILLs across 7 processes; the eighth finishes.
Expected and obtained result: **944 verified · 15 failed · 41 blocked**, zero duplicates (each write has at most one transaction in the model), 8 distinct fencing tokens.
The log below is the literal program output of that version and is kept verbatim (the program's messages were in Spanish at the time).
```
$ agenthost escenario <dir>
escenario 6d05df091eb3aecc8979 · 1000 operaciones · 988 escrituras · 1030 dependencias
$ agenthost ejecutar … [BIMBRIDGE_AH_FAULT=runner.entre_operaciones#120] → código 137 (SIGKILL)

  tras la caída: 115/1000 verificadas · 4 fallidas · 881 pendientes · seq 356 · siguiente op-64890752c1db45cb9bcf
$ agenthost ejecutar … [BIMBRIDGE_AH_FAULT=backend.mitad_tx@op-514fdec779127df119c7:7] → código 137 (SIGKILL)
  diag info:checkpoint_atrasado — checkpoint en seq 352, diario en 356; se usa el diario
  tras la caída: 154/1000 verificadas · 1 en curso · 5 fallidas · 1 bloqueadas · 839 pendientes · seq 479 · siguiente op-514fdec779127df119c7
$ agenthost ejecutar … [BIMBRIDGE_AH_FAULT=backend.tras_commit@op-7e42e1147bce7753a976] → código 137 (SIGKILL)
  diag info:checkpoint_atrasado — checkpoint en seq 457, diario en 479; se usa el diario
  tras la caída: 211/1000 verificadas · 1 en curso · 7 fallidas · 5 bloqueadas · 776 pendientes · seq 665 · siguiente op-7e42e1147bce7753a976
$ agenthost ejecutar … [BIMBRIDGE_AH_FAULT=runner.en_checkpoint#2] → código 137 (SIGKILL)
  diag info:checkpoint_atrasado — checkpoint en seq 631, diario en 665; se usa el diario
  tras la caída: 243/1000 verificadas · 1 en curso · 8 fallidas · 6 bloqueadas · 742 pendientes · seq 767 · siguiente op-98772cf51a3ccd293035
$ agenthost ejecutar … [BIMBRIDGE_AH_FAULT=runner.tras_ejecutar@op-a2516f862e1d8f1d2734] → código 137 (SIGKILL)
  diag aviso:checkpoint_a_medias — estado.json.tmp-caida: escritura interrumpida, descartada
  diag info:checkpoint_atrasado — checkpoint en seq 717, diario en 767; se usa el diario
  tras la caída: 283/1000 verificadas · 1 en curso · 8 fallidas · 7 bloqueadas · 701 pendientes · seq 894 · siguiente op-a2516f862e1d8f1d2734
$ agenthost ejecutar … [BIMBRIDGE_AH_FAULT=runner.en_verificacion@op-adafc66676d15cdf3409] → código 137 (SIGKILL)
  diag info:checkpoint_atrasado — checkpoint en seq 869, diario en 894; se usa el diario
  tras la caída: 320/1000 verificadas · 9 fallidas · 11 bloqueadas · 660 pendientes · seq 1016 · siguiente op-adafc66676d15cdf3409
$ agenthost ejecutar … [BIMBRIDGE_AH_FAULT=runner.tras_verificar@op-b31f23f33fe5e1ea0480] → código 137 (SIGKILL)
  diag info:checkpoint_atrasado — checkpoint en seq 996, diario en 1016; se usa el diario
  tras la caída: 361/1000 verificadas · 9 fallidas · 11 bloqueadas · 619 pendientes · seq 1140 · siguiente op-b31f23f33fe5e1ea0480
$ agenthost ejecutar - (sin fallo, proceso 8) → código 2
  diag info:checkpoint_atrasado — checkpoint en seq 1117, diario en 1140; se usa el diario
  944/1000 verificadas · 15 fallidas · 41 bloqueadas · seq 2936
caídas registradas:
  runner.entre_operaciones
  backend.mitad_tx@op-514fdec779127df119c7:7
  backend.tras_commit@op-7e42e1147bce7753a976
  runner.en_checkpoint
  runner.tras_ejecutar@op-a2516f862e1d8f1d2734
  runner.en_verificacion@op-adafc66676d15cdf3409
  runner.tras_verificar@op-b31f23f33fe5e1ea0480
tiempo total (8 procesos): 18465 ms
$ agenthost estado <estado> informe
  {"resumen":"944/1000 verificadas · 15 fallidas · 41 bloqueadas · seq 2936","sin_resolver":56,"por_estado":{"verificada":944,"fallida":15,"bloqueada":41},"por_error":{"verificacion":6,"dependencia":41,"rechazo_backend":3,"transitorio":2,"sin_objetivos":4},"detalle":"/tmp/bimbridge-esc1000-4334856788/estado/informe.json","sha256_detalle":"50a7fa000bd2391e4320afd63f195aea1bd2ef79ca4f63864e08d3237e94f6b4"}
$ agenthost estado <estado> excepciones
  op-4af6789bd8d3b9d395e8 fallida(sin_objetivos): el alcance no selecciona ningún elemento; no se escribió nada (permitir_vacio=false)
  op-a6d2d81662945b38cfb4 fallida(sin_objetivos): el alcance no selecciona ningún elemento; no se escribió nada (permitir_vacio=false)
  op-08c0b4d1e0dfe022760d fallida(verificacion): elementos: esperado 21, obtenido 20
  op-51bfa144a183de1627de fallida(transitorio): rechazo transitorio (simulado, intento 2); nada se aplicó
  op-6d1dda139d0ea39c26ec fallida(verificacion): elementos: esperado 21, obtenido 20
  op-7cb1e1e9fe3493587421 fallida(rechazo_backend): el backend rechazó la operación (simulado); nada se aplicó
  op-89aa63fabd2b0500787f fallida(verificacion): elementos: esperado 21, obtenido 20
  op-932a53605bc3f17dc170 fallida(verificacion): elementos: esperado 21, obtenido 20
  op-ac41361f38103e076dd5 fallida(transitorio): rechazo transitorio (simulado, intento 2); nada se aplicó
  op-c924c6b9ee1027190809 fallida(sin_objetivos): el alcance no selecciona ningún elemento; no se escribió nada (permitir_vacio=false)
  op-6efa3a1ff4ecd418bf37 fallida(verificacion): postcondición no satisfecha: desviaciones = 2
  op-eeb333d11e61f584fff2 fallida(sin_objetivos): el alcance no selecciona ningún elemento; no se escribió nada (permitir_vacio=false)
  op-bb82d5e91e110143d021 fallida(rechazo_backend): el backend rechazó la operación (simulado); nada se aplicó
  op-fb3cc5c3c9ff912b260a fallida(verificacion): elementos: esperado 21, obtenido 20
  op-fc6f9299fcdc65584f78 fallida(rechazo_backend): el backend rechazó la operación (simulado); nada se aplicó
  41 bloqueadas por dependencias (consecuencia de las anteriores)
recuperaciones en el diario:
  op-514fdec779127df119c7 {"efecto":"NoObservado","transacciones":0,"detalle":"ninguna transacción confirmada de esta operación"}
  op-7e42e1147bce7753a976 {"efecto":"Observado","transacciones":1,"detalle":"transacción tx-1-210"}
  op-98772cf51a3ccd293035 {"efecto":"NoObservado","transacciones":0,"detalle":"ninguna transacción confirmada de esta operación"}
  op-a2516f862e1d8f1d2734 {"efecto":"Observado","transacciones":1,"detalle":"transacción tx-1-283"}
dueños (fencing): 1, 2, 3, 4, 5, 6, 7, 8
líneas del diario: 2936 · transacciones del modelo: 940
```
Full evidence: `evidencia/escenario_1000_diario.jsonl` (chained journal), `evidencia/escenario_1000_informe.json` (per-operation report with source and evidence).

## Final run summary
```

Test Run Failed.
Total tests: 326
     Passed: 325
     Failed: 1
 Total time: 1.2872 Minutes
```
