# Limitaciones conocidas — 0.7.0-alpha1

1. **Nada se ejecutó en Revit.** Todo es simulado; ninguna prueba es validación nativa. El adaptador Revit rechaza escribir.
2. El modelo simulado modela parámetros por categoría/nivel, no geometría ni transacciones reales de Revit.
3. Documentos: solo texto (fixtures con marcadores `=== PAGINA n ===` y `[REQ-x]`). No hay extracción de PDF/DOCX ni lectura de planos.
4. Interpretación por reglas (regex) en inglés/español para "set/fijar" y "verify"; todo lo demás va a revisión humana.
5. Tras una caída, el siguiente proceso espera a que venza el lease (30 s por defecto). Es intencional (AH-04), pero se nota.
6. Bajo CPU saturada un lease muy corto puede perderse; el sistema lo trata bien (fencing, código 4, sin escribir), pero el proceso se detiene.
7. Un solo escritor por plan y por modelo; no hay ejecución paralela dentro de un plan.
8. Tubería con nombre `CurrentUserOnly`; sin autenticación adicional ni acceso remoto.
9. No es servicio de Windows (fuera de alcance alpha1); se ejecuta como proceso.
10. Precondiciones: solo `objetivos` min/max.
11. Defecto previo de rc3: `Key_ConcurrentInitialisation_ProducesOneKey` falla de forma intermitente (carrera al crear la clave de GrantStore; falla cerrada). Ya fallaba en 7be12ad; no se corrigió por el mandato.
12. Pruebas E2E de rc3 con tiempos ajustados pueden fallar bajo carga (`HeldCommit_WaitsForUser…` 1 vez de 4).
13. No probado en Windows en esta ronda (rutas, ACL de tubería y SIGKILL→TerminateProcess).
