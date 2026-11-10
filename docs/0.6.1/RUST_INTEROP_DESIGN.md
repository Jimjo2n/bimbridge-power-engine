# Interoperación C# + Rust (diseño, R10)

**Estado:** sólo diseño. No hay código Rust en 0.6.1 y no se autoriza implementarlo aquí. Este documento fija los contratos para que la 0.7.0 pueda añadir workers sin tocar la integración con Revit.

## 1. Reparto de responsabilidades

| Responsabilidad | Dónde | Por qué |
|---|---|---|
| Todo acceso a la API de Revit (lectura, transacciones, avisos, guardar/sync/exportar) | C# en el add-in | La API sólo existe en .NET y sólo es válida en el hilo de Revit; un segundo motor de transacciones duplicaría estados y auditoría. |
| Leases, concesiones, auditoría, checkpoints, reconciliación | C# (Engine) | Ya están probados; un worker no decide autoridad ni persiste estado de tareas. |
| Extracción de geometría (sólidos, mallas, transformaciones de vínculos) | C# en el hilo de Revit | Requiere objetos vivos; se convierte a buffers planos antes de salir. |
| Cálculo puro sobre buffers: rayos contra mallas, índices espaciales (BVH/R-tree), choques, QC uno a uno sobre miles de elementos | Candidato a Rust | Sin estado de Revit, paralelizable, medible en igualdad de condiciones. |
| Decidir qué escribir en el modelo | C# (receta + motor) | El worker sólo devuelve resultados; nunca órdenes de escritura. |

## 2. Contrato

```csharp
public interface IComputeWorker : IDisposable
{
    WorkerInfo Info { get; }                       // nombre, versión del worker, versiones de contrato soportadas
    Task<ComputeResult> RunAsync(ComputeRequest req, CancellationToken ct);
}

public sealed class ComputeRequest
{
    public int ContractVersion;                    // entero; el worker rechaza versiones que no conoce (sin "mejor esfuerzo")
    public string Kernel;                          // "rayos_vertical", "choques_aabb", "manifiesto_qc"…
    public string RequestId;                       // para auditoría y cancelación
    public ReadOnlyMemory<byte> Payload;           // buffers planos (abajo)
    public int MaxMs;                              // presupuesto; al superarlo devuelve "presupuesto_agotado" con lo hecho
    public long MaxBytes;                          // tope de salida
}

public sealed class ComputeResult
{
    public string Estado;                          // ok | presupuesto_agotado | cancelado | entrada_invalida | error
    public ReadOnlyMemory<byte> Output;
    public string Error;
    public long Ms, BytesIn, BytesOut;
}
```

## 3. Datos transferibles

- **Sólo valores**, nunca objetos de Revit: `ElementId` como `long`, `UniqueId` como texto UTF-8, coordenadas como `double`.
- **Unidades:** pies internos de Revit (las mismas que la API) en todo el contrato; la conversión a mm se hace sólo en la presentación. Un campo `units = "ft"` en la cabecera evita ambigüedad.
- **Coordenadas:** del documento anfitrión. Las de vínculos se transforman en C# antes de enviar (la transformación está probada en `Xform`).
- **Identidad estable:** cada fila lleva `UniqueId` (o su índice en una tabla de cadenas enviada una vez). El resultado se devuelve por índice; C# lo vuelve a mapear.
- **Formato:** cabecera fija (magic, contract_version, kernel, recuentos) + bloques contiguos little-endian (`f64` vértices, `u32` triángulos, `u32` desplazamientos por elemento). Sin JSON en el camino caliente; JSON sólo para la respuesta de error.

## 4. IPC, fallos y cancelación

- **Proceso aparte** (`pdi-compute.exe`), lanzado por el add-in bajo demanda. Un pánico de Rust no tumba Revit.
- **Transporte:** named pipe por usuario con la misma ACL y la misma trama de 16 MB que el puente (`Frame`); el cliente verifica el PID del servidor, como en `PipeServerCheck`.
- **Cancelación:** mensaje `cancel <RequestId>` por el mismo pipe; el worker comprueba entre bloques. El presupuesto `MaxMs` se aplica también en el lado C# (timeout + matar el proceso).
- **Fallo del worker** → la receta usa la implementación C# de referencia (siempre presente). El resultado indica qué motor lo calculó.
- **Auditoría:** cada petición registra `kernel`, `RequestId`, sha256 de entrada y salida (no los datos).

## 5. Costes y criterio de adopción

- Serialización estimada: una malla de 1 000 triángulos son unos 36 KB (vértices f64 compartidos + índices u32). 10 000 elementos con unos 200 triángulos cada uno son unos 70 MB, por encima del tope de trama: se troceará por lotes de elementos.
- Coste fijo por petición (lanzar proceso: una vez; ida y vuelta del pipe: <1 ms local medido para el puente) frente al ahorro de cálculo.
- **Medición de partida (0.3.0, sin Revit):** el montaje en C# de 1 442 luminarias × 9 puntos tarda 5–15 ms. A ese tamaño Rust no aporta nada. Los candidatos reales son los choques de modelo completo y el QC de decenas de miles de elementos.

## 6. Plan de benchmark comparativo

1. Mismo kernel en C# (actual) y Rust, mismos buffers de entrada guardados en disco desde una copia de práctica.
2. Tres tamaños: 1 000 / 10 000 / 100 000 elementos. Una corrida fría y 10 calientes; p50/p95, memoria máxima y bytes serializados.
3. Aceptación: resultados idénticos (mismo conjunto de pares/índices) y p50 total (serializar + IPC + cálculo) ≥30 % mejor que C# en el tamaño objetivo. Si no, Rust no entra.
4. La medición del tiempo de extracción en el hilo de Revit (que Rust no puede acelerar) es obligatoria antes del paso 1: si domina, se optimiza la extracción y no el cálculo.

## Adenda 0.7.0-alpha1 — candidato concreto
Kernel identificado: `TriMesh.Extent` (rayo vertical punto→malla para montaje). Banco C# en `src/PDI.RevitMcp.AgentHost/Kernels/MountKernelBench.cs`, comando `agenthost banco-kernel`. Línea base y decisión en `docs/0.7.0/RENDIMIENTO_0.7.0.md`: ~1,2 µs/punto con SIMD; Rust no justificado hasta medir en Revit real. Formato de entrada binario determinista y digest sha256 para comparar una futura implementación byte a byte.
