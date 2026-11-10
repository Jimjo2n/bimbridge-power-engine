# BIMBRIDGE – Power Engine

Automatización BIM local e integración de agentes de IA con Autodesk Revit mediante MCP.

C# mantiene el núcleo y el acceso a Revit; Rust procesa geometría y Python extrae información documental. El producto incluye consultas, planes tipados, ejecución dinámica de C#, recuperación y observabilidad.

**Estado:** candidato 0.9.0+commit, con aceptación nativa parcial. No hay una versión estable publicada. La documentación principal utiliza inglés técnico; esta introducción ofrece un punto de entrada en español.

- [Arquitectura y componentes](docs/PROJECT_OVERVIEW.md).
- [Requisitos, compilación y pruebas](docs/DEVELOPMENT.md).
- [Integración MCP](docs/integrations/MCP.md).
- [Modelo de confianza](docs/security/TRUST_MODEL.md).
- [Resultados y límites del rendimiento](docs/performance/BENCHMARKS.md).
- [Validación y pendientes](docs/quality/VALIDATION.md).
- [Cómo contribuir](CONTRIBUTING.md).

El C# dinámico está habilitado por defecto para agentes locales autorizados. No es un sandbox: comparte los privilegios del usuario de Revit. Las transacciones no revierten automáticamente archivos, red u otros efectos externos.

La base fuente recibida es a291cc6. El historial original se conserva en un archivo privado; GitHub recibe una importación saneada con trazabilidad. El repositorio permanece privado hasta una confirmación explícita independiente.

Licencia del código propio: GPL-3.0-only. Mantenedor: [Jimjo2n](https://github.com/Jimjo2n).
