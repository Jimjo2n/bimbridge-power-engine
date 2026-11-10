# Invariantes y prueba que los comprueba

| Id | Invariante | Prueba |
|---|---|---|
| INV-001 | Como mucho un dueño vigente por tarea entre procesos | `LeaseFencingTests.Lease_TwentyRealProcesses_ExactlyOneWins` |
| INV-002 | El fencing nunca retrocede (renovar conserva, adquirir sube, liberar no borra) | `Lease_ExpiryTakeover_*`, `Lease_RenewalKeepsFencing_*` |
| INV-003 | Un dueño con fencing viejo no puede validar ni renovar | `Lease_ExpiryTakeover_FencingStrictlyIncreases_OldOwnerRejected` |
| INV-004 | Tras perder el lease no se ejecuta ningún lote más | `TaskEngineTests.LeaseStolen_MidRun_OldRunnerStopsBeforeNextBatch` |
| INV-005 | Almacén de lease no fiable nunca se trata como libre | `Lease_CorruptFile_FailsSafe_NotTreatedAsFree`, `Lease_LockHeldByAnotherHandle_*` (sólo Windows) |
| INV-006 | Una unidad no demostrable nunca se re-ejecuta sola | pruebas de 0.6.0 en `TaskEngineTests` (caída entre commit y diario, reconciliar) |
| INV-007 | La auditoría no pierde líneas en silencio y detecta alteraciones | `AuditLogTests.*` |
| INV-008 | Código que escribe no corre sin registro de auditoría | cableado en `RequestHandler.Exec` (compila; no hay prueba sin Revit) |
| INV-009 | Una receta autónoma no usa capacidades no concedidas | `ScriptingTests.HostileCode_*`, `BuiltInRecipes_PassRestrictedAutonomousPolicy` |
| INV-010 | El puente sólo habla con el Revit anunciado | `PipeServerCheckTests` (regla; la llamada Win32 sólo compila aquí) |
