# SPDX-License-Identifier: GPL-3.0-only
"""PDF jobs: information, text/geometric extraction, ruled tables and candidate symbols.

Coordinates: pdfminer delivers the VISIBLE space of the page (/Rotate applied, origin bottom-left, PDF points).
Each result carries that space ("pagina_pt"), the normalized [0,1]×[0,1] of the visible area and the paper inches
(taking /UserUnit into account). They are never Revit coordinates: that is the drawing–model registration, in C#.
"""
import math
import re
from hashlib import sha256

from .worker import JobError, sha256_file

# ---------------- utilities ----------------


CODE_RX = re.compile(r"^[A-Z]{1,3}[0-9]{0,2}[A-Z]?(-[0-9]{1,2}[A-Z]{0,2})?$")


def _lazy():
    try:
        from pdfminer.high_level import extract_pages  # noqa: F401
        from pdfminer import layout  # noqa: F401
    except ImportError as e:
        raise JobError("NO_DISPONIBLE", "pdfminer.six is neither installed nor bundled: %s" % e)
    from pdfminer.high_level import extract_pages
    from pdfminer import layout
    return extract_pages, layout


def _open_pages(path, pages=None, laparams=None):
    """Analyzes each page in CONTENT SPACE (without /Rotate): text rotated by the page stays
    horizontal for grouping words and reading legends. Frame.to_visible then converts to visible space."""
    _, layout = _lazy()
    from pdfminer.pdfinterp import PDFResourceManager, PDFPageInterpreter
    from pdfminer.converter import PDFPageAggregator
    from pdfminer.pdfpage import PDFPage
    lp = laparams or layout.LAParams(char_margin=0.6, line_margin=0.2, word_margin=0.08, boxes_flow=None)
    idx = None if not pages else {p - 1 for p in pages}

    def gen():
        try:
            with open(path, "rb") as fp:
                rsrc = PDFResourceManager(caching=True)
                dev = PDFPageAggregator(rsrc, laparams=lp)
                interp = PDFPageInterpreter(rsrc, dev)
                for page in PDFPage.get_pages(fp, pagenos=idx):
                    page.rotate = 0
                    interp.process_page(page)
                    yield dev.get_result()
        except JobError:
            raise
        except Exception as e:  # noqa: BLE001
            raise JobError("INVALIDO", "unreadable PDF: %s" % e)
    return gen(), layout


def _raw_pages(path):
    """Page data that pdfminer.layout does not expose: /Rotate, /UserUnit, original MediaBox/CropBox and encryption."""
    try:
        from pdfminer.pdfparser import PDFParser
        from pdfminer.pdfdocument import PDFDocument, PDFEncryptionError, PDFPasswordIncorrect
        from pdfminer.pdfpage import PDFPage
        from pdfminer.pdftypes import resolve1
    except ImportError as e:
        raise JobError("NO_DISPONIBLE", "pdfminer.six not available: %s" % e)
    out = []
    try:
        with open(path, "rb") as f:
            doc = PDFDocument(PDFParser(f))
            info = {}
            for d in doc.info or []:
                for k, v in d.items():
                    v = resolve1(v)
                    if isinstance(v, bytes):
                        v = v.decode("latin-1", "replace")
                    if isinstance(v, (str, int, float)):
                        info[k] = v
            for p in PDFPage.create_pages(doc):
                uu = resolve1(p.attrs.get("UserUnit", 1)) or 1
                out.append({"mediabox": [float(x) for x in p.mediabox], "cropbox": [float(x) for x in (p.cropbox or p.mediabox)],
                            "rotate": int(p.rotate or 0) % 360, "userunit": float(uu)})
            return out, info, bool(doc.encryption)
    except (PDFEncryptionError, PDFPasswordIncorrect) as e:
        raise JobError("NO_SOPORTADO", "encrypted or password-protected PDF: %s" % e)
    except JobError:
        raise
    except Exception as e:  # noqa: BLE001
        raise JobError("INVALIDO", "unreadable PDF: %s" % e)


def _page_sel(args, n):
    pages = args.get("paginas")
    if pages is None:
        d, h = int(args.get("desde", 1)), int(args.get("hasta", n))
        pages = list(range(max(1, d), min(n, h) + 1))
    pages = [int(p) for p in pages]
    bad = [p for p in pages if p < 1 or p > n]
    if bad:
        raise JobError("INVALIDO", "pages out of range 1..%d: %s" % (n, bad[:5]))
    return pages


class Frame:
    """Page: analysis works in content space (without /Rotate); everything RETURNED is in VISIBLE
    space (with /Rotate applied, as a person sees it). norm/inches/to_visible take content coordinates."""

    def __init__(self, w, h, userunit, rotate=0, mediabox=None):
        self.cw, self.ch = float(w), float(h)
        self.rot = int(rotate or 0) % 360
        mb = mediabox or [0.0, 0.0, self.cw, self.ch]
        self.x0, self.y0, self.x1, self.y1 = [float(v) for v in mb]
        self.w, self.h = (self.ch, self.cw) if self.rot in (90, 270) else (self.cw, self.ch)
        self.uu = float(userunit or 1)

    def vis(self, x, y):
        """Same computation as pdfminer's CTM for /Rotate (interpreter.process_page)."""
        if self.rot == 90:
            return y - self.y0, self.x1 - x
        if self.rot == 180:
            return self.x1 - x, self.y1 - y
        if self.rot == 270:
            return self.y1 - y, x - self.x0
        return x, y

    def norm(self, x, y):
        vx, vy = self.vis(x, y)
        return [round(vx / self.w, 6), round(vy / self.h, 6)]

    def inches(self, x, y):
        vx, vy = self.vis(x, y)
        return [round(vx * self.uu / 72.0, 5), round(vy * self.uu / 72.0, 5)]

    def _box(self, b):
        a = self.vis(b[0], b[1]); c = self.vis(b[2], b[3])
        return [_r(min(a[0], c[0])), _r(min(a[1], c[1])), _r(max(a[0], c[0])), _r(max(a[1], c[1]))]

    def to_visible(self, o):
        """Converts (in place) the content pt coordinate keys to visible space."""
        if not self.rot:
            return o
        if isinstance(o, list):
            for v in o:
                self.to_visible(v)
        elif isinstance(o, dict):
            for k, v in list(o.items()):
                if k in ("bbox", "fuente_bbox", "fuente") and isinstance(v, list) and len(v) == 4:
                    o[k] = self._box(v)
                elif k == "p" and isinstance(v, list) and len(v) == 4:
                    a = self.vis(v[0], v[1]); c = self.vis(v[2], v[3])
                    o[k] = [_r(a[0]), _r(a[1]), _r(c[0]), _r(c[1])]
                elif k in ("centro_pt",) and isinstance(v, list) and len(v) == 2:
                    o[k] = [_r(t) for t in self.vis(v[0], v[1])]
                elif k == "pts" and isinstance(v, list):
                    o[k] = [[_r(t) for t in self.vis(q[0], q[1])] for q in v]
                elif k == "rot_deg" and isinstance(v, (int, float)):
                    o[k] = float((v + self.rot) % 180)
                elif k != "marco":
                    self.to_visible(v)
        return o

    def json(self):
        return {"ancho_pt": self.w, "alto_pt": self.h, "unidad_usuario": self.uu, "rotacion": self.rot,
                "ancho_in": round(self.w * self.uu / 72.0, 4), "alto_in": round(self.h * self.uu / 72.0, 4),
                "espacio": "visible (rotation applied), origin bottom-left, pt"}


def _r(v, k=3):
    return round(float(v), k)


def _words(layout, page, frame):
    """Words with a box (from their characters): text, pt bbox, normalized center, font size."""
    words = []

    def flush(chars):
        if not chars:
            return
        t = "".join(c.get_text() for c in chars).strip()
        if not t:
            return
        x0 = min(c.x0 for c in chars); y0 = min(c.y0 for c in chars); x1 = max(c.x1 for c in chars); y1 = max(c.y1 for c in chars)
        size = max(getattr(c, "size", 0) for c in chars)
        upright = all(getattr(c, "upright", True) for c in chars)
        words.append({"texto": t, "bbox": [_r(x0), _r(y0), _r(x1), _r(y1)], "centro_norm": frame.norm((x0 + x1) / 2, (y0 + y1) / 2),
                      "tam": _r(size, 2), "horizontal": bool(upright)})

    def walk(o):
        if isinstance(o, layout.LTTextLine):
            cur = []
            for c in o:
                if isinstance(c, layout.LTChar) and c.get_text().strip():
                    if cur and (c.x0 - cur[-1].x1) > max(1.0, 0.35 * c.size):
                        flush(cur); cur = []
                    cur.append(c)
                else:
                    flush(cur); cur = []
            flush(cur)
        elif isinstance(o, layout.LTContainer):
            for c in o:
                walk(c)

    walk(page)
    return words


def _vectors(layout, page, curve_paths=False):
    """Geometric primitives: lines, rectangles and curves (point polylines), with stroke/fill."""
    lines, rects, curves = [], [], []

    def walk(o):
        if isinstance(o, layout.LTLine):
            lines.append({"p": [_r(o.pts[0][0]), _r(o.pts[0][1]), _r(o.pts[-1][0]), _r(o.pts[-1][1])], "ancho": _r(o.linewidth, 2)})
        elif isinstance(o, layout.LTRect):
            rects.append({"bbox": [_r(o.x0), _r(o.y0), _r(o.x1), _r(o.y1)], "relleno": bool(o.fill), "trazo": bool(o.stroke), "ancho": _r(o.linewidth, 2)})
        elif isinstance(o, layout.LTCurve):
            curves.append({"pts": [[_r(x), _r(y)] for (x, y) in o.pts], "bbox": [_r(o.x0), _r(o.y0), _r(o.x1), _r(o.y1)],
                           "relleno": bool(o.fill), "cerrada": _closed(o.pts), "ancho": _r(o.linewidth, 2)})
            if curve_paths and o.original_path:
                curves[-1]["ruta"] = tuple(o.original_path)
        elif isinstance(o, layout.LTContainer) and not isinstance(o, layout.LTTextContainer):
            for c in o:
                walk(c)

    for o in page:
        walk(o)
    return lines, rects, curves


def _closed(pts):
    return len(pts) > 2 and math.hypot(pts[0][0] - pts[-1][0], pts[0][1] - pts[-1][1]) < 0.5


def _has_image(layout, page):
    def walk(o):
        if isinstance(o, layout.LTImage):
            return True
        if isinstance(o, layout.LTContainer):
            return any(walk(c) for c in o)
        return False
    return any(walk(o) for o in page)


# ---------------- sheet metadata (text extraction, not interpretation) ----------------

SHEET_RX = re.compile(r"^[A-Z]{1,2}-?\d+(?:\.\d+){0,3}[A-Z]?$")
REV_RX = re.compile(r"REV(?:ISION|\.)?\s*[:#]?\s*([A-Z]{0,4}\d+[A-Z0-9]*)", re.I)
SCALE_RX = re.compile(r"(\d+(?:/\d+)?)\"\s*=\s*1'-0\"")


def _sheet_meta(words, frame):
    """Candidates for sheet number, revision and scale with the source text and its position. Confidence = rule, not understanding."""
    meta = {"metodo": "extraccion_textual", "numero": None, "revision": None, "escala": None}
    big = sorted([w for w in words if SHEET_RX.match(w["texto"])], key=lambda w: -w["tam"])
    if big:
        meta["numero"] = {"valor": big[0]["texto"], "fuente": big[0]["bbox"], "confianza": 0.8 if big[0]["tam"] >= 12 else 0.5}
    joined = []
    # rebuild lines by height to find "REVISION: CCD7" and "SCALE: 1/8" = 1'-0""
    rows = {}
    for w in words:
        rows.setdefault(round(w["bbox"][1]), []).append(w)
    for y, ws in rows.items():
        ws.sort(key=lambda w: w["bbox"][0])
        joined.append((" ".join(w["texto"] for w in ws), ws[0]["bbox"]))
    for t, bb in joined:
        m = REV_RX.search(t)
        if m and not meta["revision"]:
            meta["revision"] = {"valor": m.group(1), "texto": t, "fuente": bb, "confianza": 0.7}
        s = SCALE_RX.search(t)
        if s and not meta["escala"]:
            frac = s.group(1)
            num, den = (frac.split("/") + ["1"])[:2]
            inches = float(num) / float(den)
            meta["escala"] = {"texto": s.group(0), "papel_in_por_pie": inches, "factor_revit": round(12.0 / inches, 4), "fuente": bb, "confianza": 0.7}
    return meta


# ---------------- jobs ----------------


def info(args, paths):
    path = paths.check(args.get("ruta"))
    raw, docinfo, enc = _raw_pages(path)
    pages = []
    it, layout = _open_pages(path, pages=None)
    text_chars = 0
    vec = 0
    for i, pg in enumerate(it):
        r = raw[i] if i < len(raw) else {"rotate": 0, "userunit": 1}
        fr = Frame(pg.width, pg.height, r["userunit"], r["rotate"], r.get("mediabox"))
        nt = sum(len(o.get_text()) for o in pg if isinstance(o, layout.LTTextContainer))
        nv = sum(1 for o in pg if isinstance(o, (layout.LTLine, layout.LTRect, layout.LTCurve)))
        text_chars += nt
        vec += nv
        pages.append(dict(fr.json(), numero=i + 1, rotacion=r["rotate"], mediabox=r.get("mediabox"), caracteres=nt, vectores=nv,
                          imagen=_has_image(layout, pg)))
    return {"sha256": sha256_file(path), "paginas": len(pages), "detalle_paginas": pages, "cifrado": enc,
            "metadatos": docinfo, "extraible": {"texto": text_chars > 0, "geometria": vec > 0},
            "aviso": None if text_chars or vec else "no extractable text or vectors: probably scanned; requires authorized visual interpretation"}


def verify(args, paths):
    """Compares an issued PDF with a positional delivery manifest and an optional receipt."""
    path = paths.check(args.get("ruta"))
    previous = paths.check(args["anterior"]) if "anterior" in args else None
    expected = args.get("esperado")
    if not isinstance(expected, list) or not expected:
        raise JobError("INVALIDO", "esperado requires a list of pages in issue order")
    expected_hash = args.get("sha256_esperado")
    if "sha256_esperado" in args and (not isinstance(expected_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected_hash)):
        raise JobError("INVALIDO", "sha256_esperado must be the hexadecimal SHA-256 of the receipt")
    for spec in expected:
        if not isinstance(spec, dict) or not spec or spec.keys() - {"textos", "papel_pt", "rgb_vectorial"}:
            raise JobError("INVALIDO", "each page requires textos, papel_pt or rgb_vectorial")
        if "textos" in spec and (not isinstance(spec["textos"], list) or not spec["textos"] or
                                any(not isinstance(s, str) or not s.strip() for s in spec["textos"])):
            raise JobError("INVALIDO", "textos requires a list of visible texts")
        if "papel_pt" in spec:
            paper = spec["papel_pt"]
            if not isinstance(paper, list) or len(paper) != 2 or any(type(n) not in (int, float) or not 0 < n <= 1e6 for n in paper):
                raise JobError("INVALIDO", "papel_pt requires positive width and height in points")
        if "rgb_vectorial" in spec:
            colors = spec["rgb_vectorial"]
            if not isinstance(colors, list) or not colors or any(
                    not isinstance(rgb, list) or len(rgb) != 3 or any(type(c) is not int or not 0 <= c <= 255 for c in rgb)
                    for rgb in colors):
                raise JobError("INVALIDO", "rgb_vectorial requires a list of RGB triplets from 0 to 255")

    digest = sha256_file(path)
    prior_digest = sha256_file(previous) if previous else None
    raw, _, _ = _raw_pages(path)
    differences = []
    if len(raw) != len(expected):
        differences.append({"tipo": "cantidad_paginas", "esperado": len(expected), "observado": len(raw)})
    if expected_hash and digest != expected_hash.lower():
        differences.append({"tipo": "recibo_distinto", "esperado": expected_hash, "observado": digest})
    if prior_digest == digest:
        differences.append({"tipo": "igual_al_anterior", "sha256": digest})

    it, layout = _open_pages(path)

    def leaves(obj):
        if isinstance(obj, layout.LTTextLine) or isinstance(obj, layout.LTCurve):
            yield obj
        elif isinstance(obj, layout.LTContainer):
            for child in obj:
                yield from leaves(child)

    def normalized(s):
        return " ".join(s.split()).casefold()

    pages = []
    for i, page in enumerate(it):
        meta = raw[i]
        mx0, my0, mx1, my1 = meta["mediabox"]
        cx0, cy0, cx1, cy1 = meta["cropbox"]
        crop = (max(0, cx0 - mx0), max(0, cy0 - my0), min(mx1, cx1) - mx0, min(my1, cy1) - my0)
        paper = [(mx1 - mx0) * meta["userunit"], (my1 - my0) * meta["userunit"]]
        if meta["rotate"] in (90, 270):
            paper.reverse()
        page_number = i + 1
        spec = expected[i] if i < len(expected) else {}
        has_image = _has_image(layout, page)
        lines, colors = [], set()
        for obj in leaves(page):
            x0, y0, x1, y1 = obj.bbox
            if isinstance(obj, layout.LTTextLine):
                if crop[0] <= x0 <= x1 <= crop[2] and crop[1] <= y0 <= y1 <= crop[3]:
                    lines.append(obj.get_text())
            elif x1 >= crop[0] and x0 <= crop[2] and y1 >= crop[1] and y0 <= crop[3]:
                for enabled, color in ((obj.stroke, obj.stroking_color), (obj.fill, obj.non_stroking_color)):
                    if enabled and isinstance(color, tuple) and len(color) == 3 and all(type(c) in (int, float) and 0 <= c <= 1 for c in color):
                        colors.add(tuple(round(c * 255) for c in color))
        text = normalized(" ".join(lines))
        for phrase in spec.get("textos", []):
            if normalized(phrase) not in text:
                differences.append({"pagina": page_number, "tipo": "texto_no_evaluable" if has_image else "texto_ausente", "esperado": phrase})
        if "papel_pt" in spec and any(abs(a - b) > 1 for a, b in zip(paper, spec["papel_pt"])):
            differences.append({"pagina": page_number, "tipo": "papel", "esperado": spec["papel_pt"], "observado": paper})
        for rgb in spec.get("rgb_vectorial", []):
            if tuple(rgb) not in colors:
                differences.append({"pagina": page_number, "tipo": "rgb_vectorial_no_encontrado", "esperado": rgb})
        pages.append({"pagina": page_number, "papel_pt": paper, "tiene_imagen": has_image, "textos_extraibles": len(lines)})

    if sha256_file(path) != digest:
        raise JobError("INVALIDO", "the PDF changed during verification; repeat once the issue is finished")
    return {"estado": "con_diferencias" if differences else "verificado", "sha256": digest, "sha256_anterior": prior_digest,
            "paginas": pages, "diferencias": differences,
            "alcance": "Extractable text within the crop and RGB color observed in vectors. The printed appearance requires visual checking."}


def extract(args, paths):
    path = paths.check(args.get("ruta"))
    raw, _, _ = _raw_pages(path)
    sel = _page_sel(args, len(raw))
    inc = set(args.get("incluir") or ["texto", "vectores", "metadatos"])
    max_items = int(args.get("max_elementos", 200000))
    it, layout = _open_pages(path, pages=sel)
    out_pages = []
    total = 0
    truncated = False
    for k, pg in enumerate(it):
        num = sel[k]
        r = raw[num - 1]
        fr = Frame(pg.width, pg.height, r["userunit"], r["rotate"], r.get("mediabox"))
        words = _words(layout, pg, fr)
        lines, rects, curves = _vectors(layout, pg)
        item = dict(numero=num, rotacion=r["rotate"], marco=fr.json())
        n = len(words) + len(lines) + len(rects) + len(curves)
        if total + n > max_items:
            truncated = True
            break
        total += n
        if "texto" in inc:
            item["palabras"] = words
        if "vectores" in inc:
            item["lineas"], item["rectangulos"], item["curvas"] = lines, rects, curves
        if "metadatos" in inc:
            item["lamina"] = _sheet_meta(words, fr)
        item["contenido"] = {"palabras": len(words), "vectores": len(lines) + len(rects) + len(curves), "imagen": _has_image(layout, pg)}
        out_pages.append(fr.to_visible(item))
    if total == 0 and not truncated:
        raise JobError("SIN_CONTENIDO", "no extractable text or geometry on the requested pages (scanned?); "
                                        "requires authorized visual interpretation, no content is invented")
    return {"sha256": sha256_file(path), "paginas": out_pages, "truncado": truncated, "clase": "extraccion_textual+geometrica",
            "nota": "Coordinates in points of the visible page space; they are not Revit coordinates."}


def tables(args, paths):
    """Tables from RULES (lines or rectangle edges forming a grid). Cell = text whose center falls inside."""
    path = paths.check(args.get("ruta"))
    raw, _, _ = _raw_pages(path)
    sel = _page_sel(args, len(raw))
    it, layout = _open_pages(path, pages=sel)
    res, descartadas = [], []
    for k, pg in enumerate(it):
        r = raw[sel[k] - 1]
        fr = Frame(pg.width, pg.height, r["userunit"], r["rotate"], r["mediabox"])
        words = _words(layout, pg, fr)
        lines, rects, _ = _vectors(layout, pg)
        hs, vs = [], []
        for l in lines:
            x0, y0, x1, y1 = l["p"]
            if abs(y0 - y1) < 0.5 and abs(x1 - x0) > 5:
                hs.append((min(x0, x1), max(x0, x1), (y0 + y1) / 2))
            elif abs(x0 - x1) < 0.5 and abs(y1 - y0) > 5:
                vs.append((min(y0, y1), max(y0, y1), (x0 + x1) / 2))
        for t in _grid_tables(hs, vs):
            ys, xs = t["ys"], t["xs"]
            row_h = sorted(ys[i] - ys[i + 1] for i in range(len(ys) - 1))
            if row_h and row_h[len(row_h) // 2] > float(args.get("max_alto_fila_pt", 54.0)) and not args.get("incluir_rejillas"):
                descartadas.append(fr.to_visible({"pagina": sel[k], "bbox": [_r(xs[0]), _r(ys[-1]), _r(xs[-1]), _r(ys[0])],
                                    "motivo": "rows of %.0f pt: probably a drawing grid, not a table" % row_h[len(row_h) // 2]}))
                continue
            cells = []
            for i in range(len(t["ys"]) - 1):
                row = []
                ytop, ybot = t["ys"][i], t["ys"][i + 1]
                for j in range(len(t["xs"]) - 1):
                    xl, xr = t["xs"][j], t["xs"][j + 1]
                    inside = [w for w in words if xl <= (w["bbox"][0] + w["bbox"][2]) / 2 <= xr and ybot <= (w["bbox"][1] + w["bbox"][3]) / 2 <= ytop]
                    inside.sort(key=lambda w: (-w["bbox"][1], w["bbox"][0]))
                    row.append(" ".join(w["texto"] for w in inside))
                cells.append(row)
            bbox = [t["xs"][0], t["ys"][-1], t["xs"][-1], t["ys"][0]]
            title = [w for w in words if bbox[0] - 5 <= w["bbox"][0] <= bbox[2] and 0 < w["bbox"][1] - bbox[3] <= 20]
            title.sort(key=lambda w: w["bbox"][0])
            res.append(fr.to_visible({"pagina": sel[k], "bbox": [_r(v) for v in bbox], "filas": len(cells), "columnas": len(cells[0]) if cells else 0,
                        "celdas": cells, "titulo": " ".join(w["texto"] for w in title) or None, "metodo": "reglas", "confianza": 0.85}))
    return {"sha256": sha256_file(path), "tablas": res, "descartadas": descartadas, "clase": "extraccion_estructural"}


def _grid_tables(hs, vs, tol=1.5):
    """Groups crossing H/V lines into grids (≥2 rows and ≥2 columns)."""
    out = []
    used_h = set()
    for i, h in enumerate(hs):
        if i in used_h:
            continue
        group_h = {i}
        group_v = set()
        changed = True
        while changed:
            changed = False
            for j, v in enumerate(vs):
                if j in group_v:
                    continue
                if any(hs[a][0] - tol <= v[2] <= hs[a][1] + tol and v[0] - tol <= hs[a][2] <= v[1] + tol for a in group_h):
                    group_v.add(j); changed = True
            for a, h2 in enumerate(hs):
                if a in group_h:
                    continue
                if any(h2[0] - tol <= vs[b][2] <= h2[1] + tol and vs[b][0] - tol <= h2[2] <= vs[b][1] + tol for b in group_v):
                    group_h.add(a); changed = True
        used_h |= group_h
        ys = sorted({round(hs[a][2], 1) for a in group_h}, reverse=True)
        xs = sorted({round(vs[b][2], 1) for b in group_v})
        if len(ys) >= 2 and len(xs) >= 2:
            out.append({"ys": ys, "xs": xs})
    return out


LEGEND_RX = re.compile(r"^([A-Z]{1,3}\d{0,3}[A-Z]?(?:-\d{1,3}[A-Z]{0,2})?)\s*[-–:]\s+(.+)$")


def symbols(args, paths):
    """
    CANDIDATE symbols: small closed shapes grouped by signature (type, size, fill). The legend (shape + "CODE - text")
    gives the meaning of each signature; a nearby label confirms or contradicts it. Nothing is classified without a legend or label:
    anything doubtful stays in "ambiguos" for an authorized agent. This is geometric extraction + rules, not vision.
    """
    path = paths.check(args.get("ruta"))
    traced = args.get("trazabilidad") is True or "decisiones" in args
    decisions = {}
    if "decisiones" in args:
        supplied = args["decisiones"]
        if not isinstance(supplied, list):
            raise JobError("INVALIDO", "decisiones requires a list keyed by id_simbolo")
        for item in supplied:
            if (not isinstance(item, dict) or not isinstance(item.get("id_simbolo"), str) or
                    item.get("decision") not in ("incluir", "excluir") or
                    not isinstance(item.get("evidencia"), str) or not item["evidencia"].strip() or
                    ("codigo" in item and (not isinstance(item["codigo"], str) or not CODE_RX.fullmatch(item["codigo"])))):
                raise JobError("INVALIDO", "each decision requires id_simbolo, incluir|excluir, evidencia and an optional codigo")
            if item["id_simbolo"] in decisions:
                raise JobError("INVALIDO", "duplicate decision for " + item["id_simbolo"])
            decisions[item["id_simbolo"]] = item
    digest = sha256_file(path) if traced else None
    raw, _, _ = _raw_pages(path)
    sel = _page_sel(args, len(raw))
    max_pt = float(args.get("max_simbolo_pt", 40.0))
    label_r = float(args.get("radio_rotulo_pt", 14.0))
    it, layout = _open_pages(path, pages=sel)
    pages = []
    for k, pg in enumerate(it):
        num = sel[k]
        r = raw[num - 1]
        fr = Frame(pg.width, pg.height, r["userunit"], r["rotate"], r["mediabox"])
        words = _words(layout, pg, fr)
        _, rects, curves = _vectors(layout, pg, curve_paths=True)
        shapes = []
        for c in curves:
            if not c["cerrada"]:
                continue
            x0, y0, x1, y1 = c["bbox"]
            w, h = x1 - x0, y1 - y0
            if 0.5 < w <= max_pt and 0.5 < h <= max_pt:
                kind = "circulo" if abs(w - h) < 0.15 * max(w, h) and len(c["pts"]) >= 4 else "curva"
                shapes.append({"tipo": kind, "bbox": c["bbox"], "relleno": c["relleno"],
                               "puntos": c.get("ruta") or tuple(map(tuple, c["pts"]))})
        for r in rects:
            x0, y0, x1, y1 = r["bbox"]
            w, h = x1 - x0, y1 - y0
            if 0.5 < w <= max_pt * 2 and 0.5 < h <= max_pt and min(w, h) < max_pt:
                shapes.append({"tipo": "rectangulo", "bbox": r["bbox"], "relleno": r["relleno"]})
        unique, seen, aliases = [], {}, {}
        for i, s in enumerate(shapes):
            x0, y0, x1, y1 = s["bbox"]
            w, h = x1 - x0, y1 - y0
            s["centro"] = [_r((x0 + x1) / 2), _r((y0 + y1) / 2)]
            s["w"], s["h"] = _r(w, 2), _r(h, 2)
            s["firma"] = "%s|%s|%s|%s" % (s["tipo"], _q(max(w, h)), _q(min(w, h)), "lleno" if s["relleno"] else "hueco")
            s["rot_deg"] = 0.0 if w >= h else 90.0
            key = (s["tipo"], tuple(s["bbox"]), s["relleno"], s.get("puntos"))
            if traced:
                signature = "%s:%d:%d:%s:%s" % (digest, num, i, s["firma"], s["centro"])
                s["id"] = sha256(signature.encode()).hexdigest()[:24]
            if key in seen:
                if traced:
                    aliases.setdefault(seen[key]["id"], []).append(s["id"])
                continue
            seen[key] = s
            unique.append((i, s))
        # legend: shape with text to its right on the same line "CODE - description"
        legend = {}
        legend_idx = set()
        for i, s in unique:
            cy = s["centro"][1]
            right = [w for w in words if w["bbox"][0] > s["centro"][0] and w["bbox"][0] - s["bbox"][2] < 30 and abs((w["bbox"][1] + w["bbox"][3]) / 2 - cy) < 5]
            if not right:
                continue
            right.sort(key=lambda w: w["bbox"][0])
            line = [w for w in words if abs((w["bbox"][1] + w["bbox"][3]) / 2 - cy) < 5 and w["bbox"][0] >= right[0]["bbox"][0]]
            line.sort(key=lambda w: w["bbox"][0])
            text = " ".join(w["texto"] for w in line)
            m = LEGEND_RX.match(text)
            if m:
                legend_idx.add(i)
                prev = legend.get(s["firma"])
                if prev and prev["codigo"] != m.group(1):
                    prev["conflicto"] = True
                else:
                    legend[s["firma"]] = {"codigo": m.group(1), "descripcion": m.group(2), "fuente_bbox": s["bbox"], "texto": text}
        cands, amb = [], []
        unclassified = {}
        observed = []
        for i, s in unique:
            if i in legend_idx:
                continue
            cx, cy = s["centro"]
            near = [w for w in words if math.hypot((w["bbox"][0] + w["bbox"][2]) / 2 - cx, (w["bbox"][1] + w["bbox"][3]) / 2 - cy) <= label_r]
            near.sort(key=lambda w: math.hypot((w["bbox"][0] + w["bbox"][2]) / 2 - cx, (w["bbox"][1] + w["bbox"][3]) / 2 - cy))
            label = near[0]["texto"] if near else None
            lg = legend.get(s["firma"])
            entry = {"centro_pt": s["centro"], "centro_norm": fr.norm(cx, cy), "centro_in": fr.inches(cx, cy), "firma": s["firma"],
                     "tamano_pt": [s["w"], s["h"]], "rot_deg": s["rot_deg"], "rotulo": label}
            if traced:
                entry["id_simbolo"] = s["id"]
                entry["evidencia_pdf"] = {"forma_bbox": fr._box(s["bbox"]),
                                          "rotulo_bbox": fr._box(near[0]["bbox"]) if near else None,
                                          "leyenda_bbox": fr._box(lg["fuente_bbox"]) if lg else None}
                observed.append(entry)
            if lg and not lg.get("conflicto"):
                entry["codigo"] = lg["codigo"]
                if label is None:
                    entry["confianza"] = 0.75; entry["motivo"] = "legend signature without a nearby label"
                elif label == lg["codigo"]:
                    entry["confianza"] = 0.95; entry["motivo"] = "legend signature and label match"
                else:
                    entry["confianza"] = 0.4; entry["motivo"] = "label \"%s\" contradicts the legend \"%s\"" % (label, lg["codigo"])
                    amb.append(entry); continue
                cands.append(entry)
            elif label and any(l["codigo"] == label for l in legend.values()):
                entry["codigo"] = label; entry["confianza"] = 0.5; entry["motivo"] = "legend label on a signature different from the legend's"
                amb.append(entry)
            else:
                unclassified.setdefault(s["firma"], []).append(entry)
        # 2nd pass: signature WITHOUT a legend whose labels consistently agree (≥80 %, ≥3 shapes) on a code
        # that the legend does not assign to another signature -> medium-confidence candidate; the rest of the group, ambiguous.
        legend_codes = {v["codigo"] for v in legend.values()}
        rest = {}
        for firma, group in unclassified.items():
            labels = [e["rotulo"] for e in group if e["rotulo"]]
            top = max(set(labels), key=labels.count) if labels else None
            if top and top not in legend_codes and len(group) >= 3 and labels.count(top) >= 0.8 * len(group) and CODE_RX.match(top):
                for e in group:
                    if e["rotulo"] == top:
                        e["codigo"] = top; e["confianza"] = 0.7
                        e["motivo"] = "signature without a legend; label \"%s\" consistent on %d/%d shapes" % (top, labels.count(top), len(group))
                        cands.append(e)
                    else:
                        e["codigo"] = top; e["confianza"] = 0.3; e["motivo"] = "group labeled \"%s\" but this shape does not have that label" % top
                        amb.append(e)
            else:
                rest[firma] = len(group)
        unclassified = rest
        page = {"pagina": num, "marco": fr.json(), "leyenda": [dict(v, firma=k) for k, v in legend.items()], "candidatos": cands,
                "ambiguos": amb, "sin_leyenda": [{"firma": k, "cantidad": v} for k, v in sorted(unclassified.items())],
                "lamina": _sheet_meta(words, fr)}
        if traced:
            inventory = []
            confirmed = []
            for entry in observed:
                decision = decisions.pop(entry["id_simbolo"], None)
                for alias in aliases.get(entry["id_simbolo"], []):
                    previous = decisions.pop(alias, None)
                    if previous is None:
                        continue
                    if decision and (decision["decision"] != previous["decision"] or
                                     decision.get("codigo", entry.get("codigo")) != previous.get("codigo", entry.get("codigo"))):
                        raise JobError("INVALIDO", "incompatible decisions for overlapping strokes of " + entry["id_simbolo"])
                    decision = decision or previous
                item = dict(entry, estado="sin_revisar")
                if decision:
                    item["estado"] = "incluido" if decision["decision"] == "incluir" else "excluido"
                    item["evidencia_revision"] = decision["evidencia"]
                    if item["estado"] == "incluido":
                        code = decision.get("codigo", entry.get("codigo"))
                        if not code:
                            raise JobError("INVALIDO", "specify the confirmed code of " + entry["id_simbolo"])
                        item["codigo_confirmado"] = code
                        confirmed.append(dict(item, codigo=code, confianza=1.0))
                inventory.append(item)
            page["inventario"] = inventory
            page["confirmados"] = confirmed
        pages.append(fr.to_visible(page))
    if decisions:
        raise JobError("INVALIDO", "there are decisions from another page, revision or extraction configuration: " + ", ".join(list(decisions)[:5]))
    final_digest = sha256_file(path)
    if traced and digest != final_digest:
        raise JobError("INVALIDO", "the PDF changed during extraction")
    return {"sha256": final_digest, "paginas": pages, "clase": "extraccion_geometrica+reglas_de_leyenda",
            "nota": "Candidates, not confirmed fixtures. Ambiguities require an authorized agent or person."}


def _q(v):
    return "%.1f" % (round(float(v) * 2) / 2)


GRID_LABEL_RX = re.compile(r"^([A-Z]{1,2}(\.\d{1,2})?|\d{1,3}(\.\d{1,2})?)$")


def grids(args, paths):
    """
    Drawing grids: long lines with a bubble (circle with a short label) at one end, and intersections between named
    grids. The intersections are candidate ANCHORS for drawing–model registration (the model supplies its own by grid
    name). Coordinates in the visible page space; never Revit coordinates.
    """
    path = paths.check(args.get("ruta"))
    raw, _, _ = _raw_pages(path)
    sel = _page_sel(args, len(raw))
    min_frac = float(args.get("min_largo_frac", 0.15))
    it, layout = _open_pages(path, pages=sel)
    out = []
    for k, pg in enumerate(it):
        r = raw[sel[k] - 1]
        fr = Frame(pg.width, pg.height, r["userunit"], r["rotate"], r["mediabox"])
        words = _words(layout, pg, fr)
        lines, _, curves = _vectors(layout, pg)
        min_len = min_frac * min(fr.cw, fr.ch)
        longs = [l for l in lines if math.hypot(l["p"][2] - l["p"][0], l["p"][3] - l["p"][1]) >= min_len]
        bubbles = []
        for c in curves:
            if not c["cerrada"]:
                continue
            x0, y0, x1, y1 = c["bbox"]
            w, h = x1 - x0, y1 - y0
            if 8 <= w <= 40 and abs(w - h) < 0.15 * w:
                cx, cy, rad = (x0 + x1) / 2, (y0 + y1) / 2, w / 2
                inside = [wd for wd in words if math.hypot((wd["bbox"][0] + wd["bbox"][2]) / 2 - cx, (wd["bbox"][1] + wd["bbox"][3]) / 2 - cy) < rad]
                if len(inside) == 1 and GRID_LABEL_RX.match(inside[0]["texto"]):
                    bubbles.append({"c": (cx, cy), "r": rad, "label": inside[0]["texto"]})
        # Some exporters draw the label without a circle. This heuristic is optional and
        # only applies to pages without recognizable bubbles. It looks for isolated labels aligned
        # with the end of a long stroke and placed just beyond it.
        bare_labels = args.get("sin_globo") is True and not bubbles
        if bare_labels:
            for wd in words:
                if not GRID_LABEL_RX.fullmatch(wd["texto"]):
                    continue
                x0, y0, x1, y1 = wd["bbox"]
                radius = max(4, min(10, wd["tam"] * 0.8))
                bubbles.append({"c": ((x0 + x1) / 2, (y0 + y1) / 2), "r": radius, "label": wd["texto"]})
        found = {}
        for b in bubbles:
            cx, cy = b["c"]
            best, bd = None, None
            for l in longs:
                x0, y0, x1, y1 = l["p"]
                for ex, ey, ox, oy in ((x0, y0, x1, y1), (x1, y1, x0, y0)):
                    d_end = math.hypot(cx - ex, cy - ey)
                    if d_end > b["r"] * 1.8 + 3:
                        continue
                    L = math.hypot(ex - ox, ey - oy)
                    perp = abs((ex - ox) * (oy - cy) - (ox - cx) * (ey - oy)) / L   # distance from the center to the line
                    ahead = ((cx - ex) * (ex - ox) + (cy - ey) * (ey - oy)) / L     # the bubble lies beyond the end
                    min_ahead = 0 if bare_labels else -b["r"] * 0.2
                    if perp <= b["r"] * 0.6 and ahead > min_ahead and (bd is None or d_end < bd):
                        best, bd = l, d_end
            if best is None:
                continue
            prev = found.get(b["label"])
            if prev and prev["linea"] is not best:
                prev["conflicto"] = True
                continue
            found[b["label"]] = {"eje": b["label"], "linea": best, "globo_pt": [_r(cx), _r(cy)], "conflicto": False}
        axes = [g for g in found.values() if not g["conflicto"]]
        inter = []
        for i in range(len(axes)):
            for j in range(i + 1, len(axes)):
                a, b = axes[i]["linea"]["p"], axes[j]["linea"]["p"]
                d1 = (a[2] - a[0], a[3] - a[1]); d2 = (b[2] - b[0], b[3] - b[1])
                den = d1[0] * d2[1] - d1[1] * d2[0]
                n1, n2 = math.hypot(*d1), math.hypot(*d2)
                if abs(den) < math.sin(math.radians(20)) * n1 * n2:
                    continue   # nearly parallel: they do not give a reliable anchor
                t = ((b[0] - a[0]) * d2[1] - (b[1] - a[1]) * d2[0]) / den
                u = ((b[0] - a[0]) * d1[1] - (b[1] - a[1]) * d1[0]) / den
                if -0.02 <= t <= 1.02 and -0.02 <= u <= 1.02:
                    x, y = a[0] + t * d1[0], a[1] + t * d1[1]
                    pair = sorted([axes[i]["eje"], axes[j]["eje"]], key=lambda s: (not s[0].isalpha(), s))
                    ang = math.degrees(math.acos(min(1.0, abs(d1[0] * d2[0] + d1[1] * d2[1]) / (n1 * n2))))
                    inter.append({"ejes": pair, "nombre": "/".join(pair), "punto_pt": [_r(x), _r(y)], "angulo_deg": _r(ang, 2)})
        if bare_labels:
            crossing = {label for anchor in inter for label in anchor["ejes"]}
            axes = [g for g in axes if g["eje"] in crossing]
        axes_out = [{"eje": g["eje"], "p": g["linea"]["p"], "globo_pt": g["globo_pt"]} for g in axes]
        page = {"pagina": sel[k], "marco": fr.json(), "ejes": axes_out, "intersecciones": inter,
                "conflictos": sorted(g["eje"] for g in found.values() if g["conflicto"])}
        if bare_labels:
            page["metodo_ejes"] = "rotulos_sin_globo"
        fr.to_visible(page)
        for x in page["intersecciones"]:
            x["punto_pt"] = [_r(t) for t in fr.vis(*x["punto_pt"])] if fr.rot else x["punto_pt"]
        for g in page["ejes"]:
            g["globo_pt"] = [_r(t) for t in fr.vis(*g["globo_pt"])] if fr.rot else g["globo_pt"]
        out.append(page)
    return {"sha256": sha256_file(path), "paginas": out, "clase": "extraccion_geometrica+reglas_de_ejes",
            "nota": "Candidate anchors by grid name. Registration requires the same intersections in the model."}
