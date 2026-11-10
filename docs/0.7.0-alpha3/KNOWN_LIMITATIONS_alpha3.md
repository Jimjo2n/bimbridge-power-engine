# Limitaciones conocidas de alpha3

1. **NRT-04 no se ha ejecutado aún en Revit.** Todo lo nativo de alpha3 se ha probado contra un add-in simulado (FakeBim) que habla el protocolo real por named pipe. La aceptación nativa queda pendiente en el equipo de prueba con una copia desechable.
2. **NRT-03 nativo queda pendiente para beta2.** La versión simulada (caída del AgentHost en 5 puntos) pasa. En nativo sólo se debe matar el AgentHost, nunca Revit.
3. **El catálogo real cubre sólo `consultar` y `fijar_parametro`.** `fijar_parametro` acepta sólo `unidades=texto` y como máximo 30 objetivos, porque el diff del add-in devuelve 30 filas. `verificar_qc` no está soportado contra Revit.
4. **El modo `persistente` está implementado y probado sólo con el simulador.** No se usará sobre modelos reales hasta beta1, y siempre sobre copias locales que no sean workshared.
5. **La identidad del documento depende de `clave_doc` y `huella_doc` del add-in.** Si dos copias tienen la misma ruta y la misma huella, no se distinguen. Mitigación: el arnés usa el PID y comprueba que el documento no sea workshared.
6. **La clave de concesiones sigue en Base64 bajo la ACL del perfil.** DPAPI queda para beta1.
7. **El nivel del add-in en el PC es RPW.** Reducirlo a R cuando no se escribe (mínimo privilegio) depende de la decisión del operador.
8. **El candado `clave.bin.lock` queda como archivo vacío en la carpeta de autorizaciones.** Es inocuo, porque el bloqueo lo libera el sistema operativo.
9. **`exec ensayo` sigue ejecutando código C# arbitrario.** Se revierte siempre, pero no sustituye a una operación tipada y no se usa en NRT-04.
10. **Revertir un TransactionGroup deja `IsModified=false`** (observado en NRT-04 run4). Corrección P0:
    - `commit modo=ensayo` y `exec modo=ensayo` se rechazan en el add-in, en el hilo de Revit y antes de abrir el grupo, si el documento tiene `IsModified=true` (`TrialGuard`). Vale para cualquier cliente MCP.
    - La captura temporal (`cap`) y el QC de tarea ya no abren su grupo temporal si hay cambios sin guardar.
    - Ruta no evitable: un commit **real** que falla y se revierte sobre un documento modificado. No se puede bloquear sin impedir el trabajo normal, así que se informa con `marca_cambios_sin_guardar_perdida` y un aviso.
    - Sigue sin protección: los fallos de `exec ejecutar` y de los pasos de tarea con transacción.
    - No se ha verificado en nativo si revertir **una transacción suelta** (no un grupo) también borra la marca.
    - Nunca se escribe `IsModified`; hay una prueba que lo comprueba.
11. **El `rev` del add-in sube con transacciones revertidas** (cuenta `DocumentChanged`; solo invalida cachés). El criterio revisado es `rev_no_retrocede`. run4 sigue FALLIDA en el historial, porque el criterio se cambió después.
