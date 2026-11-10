# SPDX-License-Identifier: GPL-3.0-only
"""BIMBRIDGE Power Engine — Python Worker (inteligencia documental).

Proceso aislado de Revit: recibe trabajos documentales explícitos por stdin (JSON por líneas, contrato Power Engine v1) y
devuelve datos estructurados. No llama a Revit API, no orquesta nada, no abre rutas no autorizadas y no usa la red.
Distingue EXTRACCIÓN TEXTUAL, EXTRACCIÓN GEOMÉTRICA e INTERPRETACIÓN: nunca presenta texto extraído como comprensión del plano.
"""
__version__ = "0.9.0"
PROTOCOL = 1
