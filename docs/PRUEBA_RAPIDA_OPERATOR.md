# Probar una tarea real con 0.6.0 (15 minutos, copia de práctica)

1. Instalar `PDI.RevitMcp.Setup.exe` del zip 0.6.0 (marque 2024 y/o 2026). Abrir Revit con el **modelo de práctica**.
2. Guardar una copia: pida a la IA `act "guardar_como como=%USERPROFILE%\Documents\PDI_practica\prueba_060.rvt"`.
3. Elija un RCP de la copia con luces sin etiqueta y una hoja que lo contenga. Pida a la IA, con sus palabras:
   *"Inicia la tarea etiquetar_luces id=prueba1 en la vista <id> con PDF de la hoja <número> en Documentos\PDI_practica."*
4. Mientras corre, **cierre la conversación**. Abra otra (o Codex) y diga: *"¿Cómo va la tarea prueba1?"*. Debe contestar dónde quedó sin que usted le explique nada.
5. Opcional, la prueba dura: cierre Revit desde el Administrador de tareas a mitad. Reabra la copia y pida *"reanuda la tarea prueba1"*.
6. Al final compruebe: cada luz con **una sola** etiqueta, el PDF nuevo en Documentos\PDI_practica, y la IA le da el hash del PDF y del guardado.
7. Si algo sale raro, ejecute `aceptacion\recoger_evidencia.ps1 -Tareas prueba1` y mande el zip del Escritorio al hilo.

No use el modelo de producción ni el central para esta prueba.
