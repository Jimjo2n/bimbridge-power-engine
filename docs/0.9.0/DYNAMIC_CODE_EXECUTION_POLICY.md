# Política de ejecución dinámica de C#

Estado: NATIVE_ACCEPTANCE_PENDING para esta revisión.

## Comportamiento

| Control | Valor por defecto | Efecto |
|---|---|---|
| **Bloquear inyección de código C#** (cinta BIMBRIDGE, panel Autoridad) | desactivado | `exec` corre para los agentes locales autorizados |
| Activado | — | Todo `exec` nuevo (consulta, ensayo, ejecutar) se rechaza con `inyeccion_csharp_bloqueada` antes de compilar |

- Persiste en `%APPDATA%\PDI.RevitMcp\config.json` como `"bloquear_inyeccion_csharp": true|false`. Si el archivo está dañado, se trata como bloqueado (fail-closed).
- Si no se puede guardar el cambio, el botón no lo aplica y lo dice.
- Se aplica a las peticiones nuevas. No interrumpe lo que ya entró en el hilo de Revit.
- Queda en la auditoría (`bloquear_inyeccion_csharp: bloqueado|permitido`), con la persona como autor.

## Quién es un «agente local autorizado»

1. Un proceso del **mismo usuario de Windows**. La tubería usa `PipeOptions.CurrentUserOnly` y una ACL que sólo admite a ese usuario. No hay escucha de red.
2. Con el **nivel** de `config.json`: `exec ejecutar` y `ensayo` exigen W; `consulta` exige R. `runner: apagado` sigue apagando todo `exec`.

Desactivar el bloqueo **no** abre `exec` a conexiones externas ni anónimas: no existen.

## Qué se eliminó

- «Código confiable» de 4 horas, sólo en memoria. Lo sustituye el control único y persistente.
- La autorización por SHA-256 de cada script (`codigo_autorizado`) en la ruta de `exec`. `ProfilePolicy.DenyDynamicCode` sigue en el código, pero ya no se llama desde `exec`.

## Qué se conserva

- Auditoría durable antes de correr código que escribe; sin auditoría no se ejecuta.
- `ReferencePolicy` para DLL extra (`refs=`).
- Recetas y tareas autónomas con el perfil restringido y sus concesiones.
- Reglas de hilo de Revit: la compilación con Roslyn va fuera del hilo; la ejecución, en el `ExternalEvent` del dispatcher.
- Caché de compilación existente (`ScriptHost`, clave por contenido). No se amplió en esta revisión.

## Límites (no es un sandbox)

El código corre en el proceso de Revit con los permisos del usuario. Un `TransactionGroup` revierte cambios del documento, no archivos, red, guardados ni otros documentos. Las operaciones tipadas (`plan`/`commit`) siguen siendo la vía con garantías verificables: diff, conflicto, intención durable, reconciliación.

**Riesgo de organización:** habilitar C# por defecto puede chocar con la política de seguridad del entorno donde se instala. La configuración debe revisarse antes del uso en producción.
