# Rendimiento 0.6.1 (sin Revit)

| Medida | Valor | Cómo |
|---|---|---|
| ValidateOwnership (bloqueo + lectura) | 0.0689 ms por llamada | 1 000 llamadas, Linux, disco local del contenedor |
| RenewLease (bloqueo + escritura atómica) | 2.3653 ms por llamada | 1 000 llamadas |
| Coste por lote | 1 validación por lote; renovación cada 20 s | TaskRunner |
| Análisis de capacidades | se hace sobre el árbol ya compilado; sin compilación extra | Compiler.CompileWithCapabilities |
| Tokens | sin cambios en las herramientas: el perfil compacto sigue en 5 685 caracteres | bench tokens de 0.6.0 |

No es una medida de Windows ni de Revit. En Windows con antivirus la apertura de archivo puede costar más; se medirá en la aceptación nativa.
