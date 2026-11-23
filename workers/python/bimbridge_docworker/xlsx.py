# SPDX-License-Identifier: GPL-3.0-only
"""Reads feeder schedule rows from a local XLSX, with per-cell evidence."""
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
        raise JobError("TOO_LARGE", "XLSX part larger than 16 MB: " + name)
    data = book.read(member)
    markup = data.replace(b"\0", b"").upper()
    if b"<!DOCTYPE" in markup or b"<!ENTITY" in markup:
        raise JobError("INVALID", "XLSX with DTD or entities")
    return ET.fromstring(data)


def _text(node):
    return "".join(part.text or "" for part in node.iter(MAIN + "t"))


def _value(cell, strings):
    kind = cell.get("t")
    value = cell.findtext(MAIN + "v")
    if kind == "inlineStr":
        return _text(cell).strip()
    if kind == "e" or (cell.find(MAIN + "f") is not None and value is None):
        raise JobError("INVALID", "cell without a valid computed value: " + cell.get("r", "?"))
    if kind == "s":
        index = int(value)
        if index < 0 or index >= len(strings):
            raise JobError("INVALID", "text index out of range at " + cell.get("r", "?"))
        return strings[index].strip()
    return (value or "").strip()


def read_feeders(args, paths):
    path = paths.check(args.get("path"))
    sheet_name = args.get("sheet")
    if not isinstance(sheet_name, str) or not sheet_name.strip():
        raise JobError("INVALID", "specify the Excel sheet that contains the feeder schedule")
    # default headers match the Spanish column names of existing feeder schedules
    columns = args.get("columns", {"id": "ID", "origin": "Origen", "target": "Destino", "open": "Abierto"})
    if not isinstance(columns, dict) or any(not isinstance(columns.get(k), str) or not columns[k].strip() for k in ("id", "origin", "target")):
        raise JobError("INVALID", "columns must identify the id, origin and target headers")
    headers = {k: v.strip().casefold() for k, v in columns.items() if k in ("id", "origin", "target", "open") and isinstance(v, str)}
    if len(set(headers.values())) != len(headers):
        raise JobError("INVALID", "the Excel fields must have distinct headers")
    header_row = args.get("header_row", 1)
    if type(header_row) is not int or header_row < 1:
        raise JobError("INVALID", "header_row must be a positive integer")
    first_row = args.get("start_row", header_row + 1)
    last_row = args.get("end_row", 1048576)
    if type(first_row) is not int or type(last_row) is not int or not header_row < first_row <= last_row <= 1048576:
        raise JobError("INVALID", "the range requires header_row < start_row <= end_row")
    digest = sha256_file(path)
    try:
        with zipfile.ZipFile(path) as book:
            workbook = _part(book, "xl/workbook.xml")
            sheets = [s for s in workbook.iter(MAIN + "sheet") if s.get("name") == sheet_name]
            if len(sheets) != 1:
                raise JobError("INVALID", "the requested sheet does not exist or is not unique: " + sheet_name)
            relationships = _part(book, "xl/_rels/workbook.xml.rels")
            relation = next((r for r in relationships if r.get("Id") == sheets[0].get(LINK + "id")), None)
            if relation is None or relation.get("TargetMode") == "External":
                raise JobError("INVALID", "the sheet requires an external reference")
            target = relation.get("Target", "")
            part = posixpath.normpath(target.lstrip("/") if target.startswith("/") else posixpath.join("xl", target))
            if not part.startswith("xl/worksheets/"):
                raise JobError("INVALID", "XLSX target is not an internal sheet")
            strings = []
            if "xl/sharedStrings.xml" in book.namelist():
                strings = [_text(item) for item in _part(book, "xl/sharedStrings.xml").iter(MAIN + "si")]
            rows = list(_part(book, part).iter(MAIN + "row"))
            if len(rows) > 50000:
                raise JobError("TOO_LARGE", "the sheet exceeds 50,000 rows")
            output = []
            positions = None
            for row in rows:
                index = int(row.get("r", 0))
                if index != header_row and not first_row <= index <= last_row:
                    continue
                if index == header_row:
                    header_cells = {}
                    for cell in row.findall(MAIN + "c"):
                        address = cell.get("r", "")
                        key = _value(cell, strings).casefold()
                        if key in headers.values():
                            if key in header_cells:
                                raise JobError("INVALID", "repeated header: " + key)
                            header_cells[key] = address.rstrip("0123456789")
                    positions = {key: header_cells[value] for key, value in headers.items() if value in header_cells}
                    if any(key not in positions for key in ("id", "origin", "target")):
                        raise JobError("INVALID", "the header row lacks id, origin or target")
                    continue
                if positions is None:
                    raise JobError("INVALID", "header row not found")
                cells = {cell.get("r"): cell for cell in row.findall(MAIN + "c")}
                addresses = {key: col + str(index) for key, col in positions.items()}
                values = {key: _value(cells[address], strings) if address in cells else "" for key, address in addresses.items()}
                if not any(values.values()):
                    continue
                open_end = values.get("open", "").casefold() in ("sí", "si", "true", "1", "abierto")
                if not values["id"] or not values["origin"] or (not values["target"]) != open_end:
                    raise JobError("INVALID", "incomplete feeder or unsupported open end in row %d" % index)
                output.append({"id": values["id"], "origin": values["origin"], "target": values["target"] or None,
                               "open": open_end, "source": {"sheet": sheet_name, "row": index, "cells": addresses}})
            if not output:
                raise JobError("INVALID", "the sheet contains no feeders")
    except (zipfile.BadZipFile, ET.ParseError, KeyError, TypeError, ValueError) as error:
        raise JobError("INVALID", "unreadable XLSX: " + str(error))
    if digest != sha256_file(path):
        raise JobError("INVALID", "the XLSX changed while it was being read")
    return {"sha256": digest, "sheet": sheet_name, "edges": output}
