# SPDX-License-Identifier: GPL-3.0-only
"""Python Worker entry point. Launched with `python -I -B run_worker.py` (isolated mode:
no PYTHONPATH, no user site, no current directory). Adds ONLY its own folder."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bimbridge_docworker.worker import main  # noqa: E402

if __name__ == "__main__":
    main()
