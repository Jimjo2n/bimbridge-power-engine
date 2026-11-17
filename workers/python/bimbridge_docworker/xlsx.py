# SPDX-License-Identifier: GPL-3.0-only
"""Lee filas del cuadro de alimentadores de un XLSX local, con evidencia por celda."""
import posixpath
import zipfile
import xml.etree.ElementTree as ET

from .worker import JobError, sha256_file

MAIN = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
LINK = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"
MAX_PART = 16 * 1024 * 1024


def _part(book, name):
    member = book.getinfo(name)
    if member.file_size > MAX_PART:
        raise JobError("DEMASIADO_GRANDE", "parte XLSX mayor de 16 MB: " + name)
    data = book.read(member)
    markup = data.replace(b"\0", b"").upper()
    if b"<!DOCTYPE" in markup or b"<!ENTITY" in markup:
        raise JobError("INVALIDO", "XLSX con DTD o entidades")
    return ET.fromstring(data)


def _text(node):
    return "".join(part.text or "" for part in node.iter(MAIN + "t"))


def _value(cell, strings):
    kind = cell.get("t")
    value = cell.findtext(MAIN + "v")
    if kind == "inlineStr":
        return _text(cell).strip()
    if kind == "e" or (cell.find(MAIN + "f") is not None and value is None):
        raise JobError("INVALIDO", "celda sin valor calculado válido: " + cell.get("r", "?"))
    if kind == "s":
        index = int(value)
        if index < 0 or index >= len(strings):
            raise JobError("INVALIDO", "índice de texto fuera de rango en " + cell.get("r", "?"))
        return strings[index].strip()
    return (value or "").strip()


def read_feeders(args, paths):
    path = paths.check(args.get("ruta"))
    sheet_name = args.get("hoja")
    if not isinstance(sheet_name, str) or not sheet_name.strip():
        raise JobError("INVALIDO", "indique la hoja Excel que contiene el feeder schedule")
    columns = args.get("columnas", {"id": "ID", "origen": "Origen", "destino": "Destino", "abierto": "Abierto"})
    if not isinstance(columns, dict) or any(not isinstance(columns.get(k), str) or not columns[k].strip() for k in ("id", "origen", "destino")):
        raise JobError("INVALIDO", "columnas debe identificar los encabezados id, origen y destino")
    headers = {k: v.strip().casefold() for k, v in columns.items() if k in ("id", "origen", "destino", "abierto") and isinstance(v, str)}
    if len(set(headers.values())) != len(headers):
        raise JobError("INVALIDO", "los campos Excel deben tener encabezados distintos")
    digest = sha256_file(path)
    try:
        with zipfile.ZipFile(path) as book:
            workbook = _part(book, "xl/workbook.xml")
            sheets = [s for s in workbook.iter(MAIN + "sheet") if s.get("name") == sheet_name]
            if len(sheets) != 1:
                raise JobError("INVALIDO", "la hoja solicitada no existe o no es única: " + sheet_name)
            relationships = _part(book, "xl/_rels/workbook.xml.rels")
            relation = next((r for r in relationships if r.get("Id") == sheets[0].get(LINK + "id")), None)
            if relation is None or relation.get("TargetMode") == "External":
                raise JobError("INVALIDO", "la hoja requiere una referencia externa")
            target = relation.get("Target", "")
            part = posixpath.normpath(target.lstrip("/") if target.startswith("/") else posixpath.join("xl", target))
            if not part.startswith("xl/worksheets/"):
                raise JobError("INVALIDO", "destino XLSX no corresponde a una hoja interna")
            strings = []
            if "xl/sharedStrings.xml" in book.namelist():
                strings = [_text(item) for item in _part(book, "xl/sharedStrings.xml").iter(MAIN + "si")]
            rows = list(_part(book, part).iter(MAIN + "row"))
            if len(rows) > 50000:
                raise JobError("DEMASIADO_GRANDE", "la hoja supera 50.000 filas")
            output = []
            positions = None
            header_row = int(args.get("fila_encabezado", 1))
            for row in rows:
                index = int(row.get("r", 0))
                if index < header_row:
                    continue
                if index == header_row:
                    header_cells = {}
                    for cell in row.findall(MAIN + "c"):
                        address = cell.get("r", "")
                        key = _value(cell, strings).casefold()
                        if key in headers.values():
                            if key in header_cells:
                                raise JobError("INVALIDO", "encabezado repetido: " + key)
                            header_cells[key] = address.rstrip("0123456789")
                    positions = {key: header_cells[value] for key, value in headers.items() if value in header_cells}
                    if any(key not in positions for key in ("id", "origen", "destino")):
                        raise JobError("INVALIDO", "la fila de encabezado carece de id, origen o destino")
                    continue
                if positions is None:
                    raise JobError("INVALIDO", "no se encontró la fila de encabezado")
                cells = {cell.get("r"): cell for cell in row.findall(MAIN + "c")}
                addresses = {key: col + str(index) for key, col in positions.items()}
                values = {key: _value(cells[address], strings) if address in cells else "" for key, address in addresses.items()}
                if not any(values.values()):
                    continue
                open_end = values.get("abierto", "").casefold() in ("sí", "si", "true", "1", "abierto")
                if not values["id"] or not values["origen"] or (not values["destino"]) != open_end:
                    raise JobError("INVALIDO", "alimentador incompleto o extremo abierto sin respaldo en la fila %d" % index)
                output.append({"id": values["id"], "origen": values["origen"], "destino": values["destino"] or None,
                               "abierto": open_end, "fuente": {"hoja": sheet_name, "fila": index, "celdas": addresses}})
            if not output:
                raise JobError("INVALIDO", "la hoja no contiene alimentadores")
    except (zipfile.BadZipFile, ET.ParseError, KeyError, TypeError, ValueError) as error:
        raise JobError("INVALIDO", "XLSX ilegible: " + str(error))
    if digest != sha256_file(path):
        raise JobError("INVALIDO", "el XLSX cambió durante la lectura")
    return {"sha256": digest, "hoja": sheet_name, "aristas": output}
