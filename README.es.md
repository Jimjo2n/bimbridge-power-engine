<img src="docs/assets/github-cover.svg" width="1120" height="270" alt="">

# BIMBRIDGE · Power Engine

**Automatización BIM local e integración con IA para Autodesk Revit.** Conecta un cliente MCP para consultar modelos, revisar cambios propuestos y ejecutar flujos de trabajo de Revit desde Windows.

[English](README.md) · [Instalación](docs/getting-started/INSTALLATION.md) · [Conectar un cliente](docs/integrations/MCP.md) · [Ejemplos](examples/README.md) · [Documentación](docs/INDEX.md)

> [!NOTE]
> **Versiones experimentales.** El código se compila para Revit 2022–2026. Consulta el registro de validación para saber qué operaciones y entornos se han probado.
> [Versiones](https://github.com/Jimjo2n/bimbridge-power-engine/releases) · [Validación](docs/quality/VALIDATION.md)

## Qué puedes hacer

| Consultar el modelo | Revisar y aplicar cambios | Procesar documentos y geometría |
| --- | --- | --- |
| Filtra elementos, cuenta luminarias y obtén resúmenes compactos. | Prepara un plan tipado, inspecciona sus cambios y aplícalo con controles de conflictos. | Extrae datos de PDF y DOCX con Python y ejecuta operaciones de geometría con Rust. |

## Cómo se conecta

```mermaid
flowchart TB
  accTitle: Arquitectura de BIMBRIDGE
  accDescr: El cliente MCP se conecta al puente C sharp. El puente se comunica con el complemento de Revit por un canal local. AgentHost usa el puente y los procesos especializados de Rust y Python.
  Client["Cliente MCP"] -->|"stdio"| Bridge["Puente C#"]
  Host["AgentHost<br/>Orquestación de flujos"] --> Bridge
  Bridge -->|"Canal local de Windows"| Addin["Complemento de Revit<br/>Despachador de la API"]
  Addin --> Model["Documento Revit<br/>Consultas y transacciones"]
  Host --> Rust["Rust<br/>Geometría · FFI o IPC"]
  Host --> Python["Python<br/>Extracción de PDF y DOCX"]
  classDef engine fill:#123748,stroke:#359687,color:#f0fbff,stroke-width:2px
  classDef worker fill:#23334d,stroke:#829ac7,color:#f0fbff
  class Bridge,Addin engine
  class Rust,Python worker
```

[Arquitectura y componentes](docs/PROJECT_OVERVIEW.md) · [Modelo de confianza](docs/security/TRUST_MODEL.md)

## De una consulta a un cambio revisado

```mermaid
flowchart LR
  accTitle: Revisión y aplicación de cambios
  Inspect["1 · Consultar<br/>Identifica el documento"] --> Plan["2 · Planificar<br/>Prepara los cambios"]
  Plan --> Review["3 · Revisar<br/>Comprueba la propuesta"]
  Review --> Apply["4 · Aplicar<br/>Verifica conflictos"]
  classDef decision fill:#123748,stroke:#359687,color:#f0fbff,stroke-width:2px
  class Review decision
```

Empieza con una consulta de solo lectura. En el perfil compacto de MCP, `q LF n=true` solicita un conteo de luminarias. Para escribir en el modelo, sigue las [recetas de planificación y aplicación](docs/RECETAS.md).

> [!IMPORTANT]
> El C# dinámico se ejecuta con los privilegios del usuario de Revit. Consulta el [modelo de confianza](docs/security/TRUST_MODEL.md) para conocer los controles de ejecución y los límites de las transacciones.

## Primeros pasos

1. **Instala en Windows.** Sigue la [guía de instalación](docs/getting-started/INSTALLATION.md) y revisa el estado del paquete elegido.
2. **Conecta tu cliente MCP.** Sigue la [guía de integración](docs/integrations/MCP.md) y prueba una consulta con los [ejemplos](examples/README.md).
3. **Compila desde el código fuente.** Consulta los [requisitos y comandos](docs/DEVELOPMENT.md). Los objetivos de Revit 2022–2024 usan .NET Framework 4.8 y los de 2025–2026 usan .NET 8.

## Evidencia y mantenimiento

| Para… | Consulta |
| --- | --- |
| Revisar qué se ha probado | [Validación](docs/quality/VALIDATION.md) · [Controles de GitHub](docs/quality/CI.md) |
| Evaluar el rendimiento | [Mediciones y metodología](docs/performance/BENCHMARKS.md) |
| Seguir el desarrollo | [Roadmap](docs/roadmap/BACKLOG.md) · [Estado del desarrollo](DEVELOPMENT_STATE.md) |
| Consultar el origen del código | [Registro de procedencia](docs/REPOSITORY_SETUP.md) |
| Contribuir o reportar un problema | [Contribuciones](CONTRIBUTING.md) · [Reportes de seguridad](SECURITY.md) |

Mantenedor [Jose Jimenez](https://github.com/Jimjo2n). Código propio bajo [GPL-3.0-only](LICENSE). Consulta los [avisos de terceros](THIRD_PARTY_NOTICES.md).
