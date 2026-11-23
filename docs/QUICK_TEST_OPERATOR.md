# Try a real task with 0.6.0 (15 minutes, practice copy)

1. Install `Bimbridge.Setup.exe` from the 0.6.0 zip (check 2024 and/or 2026). Open Revit with the **practice model**.
2. Save a copy: ask the AI `act "guardar_como como=%USERPROFILE%\Documents\BIMBRIDGE_practica\prueba_060.rvt"`.
3. Pick an RCP in the copy with untagged fixtures and a sheet that contains it. Ask the AI, in your own words:
   *"Start the tag_lights task id=prueba1 in view <id> with a PDF of sheet <number> in Documents\BIMBRIDGE_practica."*
4. While it runs, **close the conversation**. Open another one (or Codex) and say: *"How is task prueba1 going?"*. It must answer where it stands without you explaining anything.
5. Optional, the hard test: kill Revit from Task Manager halfway through. Reopen the copy and ask *"resume task prueba1"*.
6. At the end, check: each fixture has **exactly one** tag, the new PDF is in Documents\BIMBRIDGE_practica, and the AI gives you the hash of the PDF and of the save.
7. If anything looks wrong, run `acceptance\collect_evidence.ps1 -Tasks prueba1` and send the zip from the Desktop to the thread.

Do not use the production model or the central model for this test.
