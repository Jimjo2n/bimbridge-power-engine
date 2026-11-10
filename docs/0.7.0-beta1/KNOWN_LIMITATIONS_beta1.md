# Limitaciones conocidas de beta1

1. **NRT-05 nativo aún no se ejecutó.** Todo lo de beta1 está probado sólo contra un add-in falso. No se declara éxito hasta correrlo en la instancia de prueba autorizada.
2. **La "reapertura" es una verificación en disco, no un cierre y reapertura del documento activo.** La API de Revit no puede cerrar el documento activo, y el procedimiento utiliza una instancia de prueba aislada. Lo que se hace es abrir una copia del archivo recién guardado como otro Document. Esto prueba lo que está en disco, pero no un ciclo de cerrar y abrir por la interfaz. Ese ciclo requiere una operación manual seguida de `verificar_disco`.
3. **verificar_disco sobre modelos grandes.** Abrir la copia cuesta lo mismo que abrir el modelo. Con el modelo de muestra de Dynamo es aceptable; con modelos corporativos no debe usarse (además está fuera de alcance).
4. **Comprobación de duplicados en NRT-05.** Se reanuda el mismo diario y se cuentan los commits. La reenviada de la misma clave al add-in con argumentos distintos (conflicto) está cubierta por las pruebas del diario del add-in, no por el arnés nativo.
5. **El instalador sólo lectura** se probó en Linux. Ahí el atributo ReadOnly casi no se aplica, así que la validación real de `CopyOver` es la próxima actualización en Windows.
6. **Rendimiento en Revit** (latencias de commit, guardado y verificar_disco) sólo existe cuando corre NRT-05 nativo; ver PERFORMANCE_beta1.md.
7. Siguen vigentes las limitaciones de alpha3: rev cuenta las transacciones revertidas, el ensayo exige IsModified=false y NRT-03 nativo está pendiente (beta2).
