# Aceptación del ensayo reversible — 0.9.0

**NATIVE_ACCEPTANCE_PENDING · AWAITING_USER_AUTHORIZATION**

## Requisitos

- Autorización explícita para el PID/instancia, ruta de una copia local desechable, versión del build y una escritura reversible concreta. La copia debe ser sintética o pública, sin worksharing y sin cambios pendientes.
- Perfil de laboratorio, niveles de acceso suficientes y documento fijado por handle. Verificar ruta, identidad, condición de sólo lectura y ausencia de cambios pendientes.
- Operación inicial acotada. Cambiar el parámetro de comentarios de un solo ejemplar de prueba a un valor distinguible. Registrar valor inicial y SHA-256 del RVT cerrado antes de la campaña.

## Ensayo

1. Obtener el valor original con una lectura dirigida. Crear un plan tipado fijado al documento y revisar el diff. El plan prepara el cambio sin aplicarlo.
2. Enviar una única llamada de escritura `commit` con ese `plan_id` y `modo=ensayo`. El hilo de Revit abre un grupo, aplica la transacción hija, verifica el valor temporal, revierte el grupo y relee el valor restaurado en esa invocación.
3. Exigir `estado=ensayado`, `temporal_ok=true`, `grupo_estado=RolledBack`, `restauracion_ok=true`, al menos una relectura y ningún elemento temporal persistente. La confirmación exige ambos indicadores booleanos.
4. Releer el parámetro de forma independiente y comparar con el original. Conservar recibos y hash de disco de la copia. Finalizar sin guardar ni sincronizar.
5. Si se pierde la respuesta, hay `desconocido` o una discrepancia, detener la campaña y reconciliar por lectura. Una llamada de Deshacer separada queda fuera de este procedimiento.

## Alcance de la evidencia offline

Los mocks ejercitan la secuencia verificación → rollback → retorno y excepciones. La compilación comprueba el adaptador. La primera campaña cubre sólo modificación de un parámetro. Creación, borrado, cambios secundarios de Revit, grupos en estado Pending y otros tipos de operación requieren evidencia específica. El ensayo de verificación dentro del grupo y la relectura posterior son parte de una sola llamada de escritura.
