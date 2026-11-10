# SPDX-License-Identifier: GPL-3.0-only
"""Punto de entrada del Python Worker. Se lanza con `python -I -B run_worker.py` (modo aislado:
sin PYTHONPATH, sin site de usuario, sin el directorio actual). Añade SOLO su propia carpeta."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bimbridge_docworker.worker import main  # noqa: E402

if __name__ == "__main__":
    main()
