# SPDX-License-Identifier: GPL-3.0-only
"""BIMBRIDGE Power Engine — Python Worker (document intelligence).

Process isolated from Revit: receives explicit document jobs on stdin (JSON lines, Power Engine contract v1) and
returns structured data. It does not call the Revit API, orchestrates nothing, opens no unauthorized paths and does not use the network.
It distinguishes TEXT EXTRACTION, GEOMETRIC EXTRACTION and INTERPRETATION: it never presents extracted text as understanding of the drawing.
"""
__version__ = "0.9.0"
PROTOCOL = 1
