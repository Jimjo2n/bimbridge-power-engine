# SPDX-License-Identifier: GPL-3.0-only
"""Trabajos PDF: información, extracción textual/geométrica, tablas con reglas y símbolos candidatos.

Coordenadas: pdfminer entrega el espacio VISIBLE de la página (rotación /Rotate aplicada, origen abajo-izquierda, puntos PDF).
Cada resultado lleva ese espacio ("pagina_pt"), el normalizado [0,1]×[0,1] del área visible y las pulgadas de papel
(teniendo en cuenta /UserUnit). Nunca son coordenadas de Revit: eso es el registro plano–modelo, en C#.
"""
import math
import re

from .worker import JobError, sha256_file

# ---------------- utilidades ----------------


CODE_RX = re.compile(r"^[A-Z]{1,3}[0-9]{0,2}[A-Z]?(-[0-9]{1,2}[A-Z]{0,2})?$")


def _lazy():
    try:
        from pdfminer.high_level import extract_pages  # noqa: F401
        from pdfminer import layout  # noqa: F401
    except ImportError as e:
        raise JobError("NO_DISPONIBLE", "pdfminer.six no está instalado ni empaquetado: %s" % e)
    from pdfminer.high_level import extract_pages
    from pdfminer import layout
    return extract_pages, layout


def _open_pages(path, pages=None, laparams=None):
    """Analiza cada página en el ESPACIO DEL CONTENIDO (sin /Rotate): el texto rotado por la página sigue siendo
    horizontal para agrupar palabras y leer leyendas. Frame.to_visible convierte después al espacio visible."""
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
            raise JobError("INVALIDO", "PDF ilegible: %s" % e)
    return gen(), layout


def _raw_pages(path):
    """Datos de página que pdfminer.layout no expone: /Rotate, /UserUnit, MediaBox/CropBox originales y cifrado."""
    try:
        from pdfminer.pdfparser import PDFParser
        from pdfminer.pdfdocument import PDFDocument, PDFEncryptionError, PDFPasswordIncorrect
        from pdfminer.pdfpage import PDFPage
        from pdfminer.pdftypes import resolve1
    except ImportError as e:
        raise JobError("NO_DISPONIBLE", "pdfminer.six no disponible: %s" % e)
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
        raise JobError("NO_SOPORTADO", "PDF cifrado o con contraseña: %s" % e)
    except JobError:
        raise
    except Exception as e:  # noqa: BLE001
        raise JobError("INVALIDO", "PDF ilegible: %s" % e)


def _page_sel(args, n):
    pages = args.get("paginas")
    if pages is None:
        d, h = int(args.get("desde", 1)), int(args.get("hasta", n))
        pages = list(range(max(1, d), min(n, h) + 1))
    pages = [int(p) for p in pages]
    bad = [p for p in pages if p < 1 or p > n]
    if bad:
        raise JobError("INVALIDO", "páginas fuera de rango 1..%d: %s" % (n, bad[:5]))
    return pages


class Frame:
    """Página: el análisis trabaja en el espacio del contenido (sin /Rotate); todo lo que se DEVUELVE está en el espacio
    VISIBLE (con /Rotate aplicado, como lo ve una persona). norm/inches/to_visible reciben coordenadas del contenido."""

    def __init__(self, w, h, userunit, rotate=0, mediabox=None):
        self.cw, self.ch = float(w), float(h)
        self.rot = int(rotate or 0) % 360
        mb = mediabox or [0.0, 0.0, self.cw, self.ch]
        self.x0, self.y0, self.x1, self.y1 = [float(v) for v in mb]
        self.w, self.h = (self.ch, self.cw) if self.rot in (90, 270) else (self.cw, self.ch)
        self.uu = float(userunit or 1)

    def vis(self, x, y):
        """Mismo cálculo que el CTM de pdfminer para /Rotate (interpreter.process_page)."""
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
        """Convierte (en sitio) las claves de coordenadas pt del contenido al espacio visible."""
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
                "espacio": "visible (rotación aplicada), origen abajo-izquierda, pt"}


def _r(v, k=3):
    return round(float(v), k)


def _words(layout, page, frame):
    """Palabras con caja (de los caracteres): texto, bbox pt, centro normalizado, tamaño de letra."""
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


def _vectors(layout, page):
    """Primitivas geométricas: líneas, rectángulos y curvas (polilíneas de puntos), con trazo/relleno."""
    lines, rects, curves = [], [], []

    def walk(o):
        if isinstance(o, layout.LTLine):
            lines.append({"p": [_r(o.pts[0][0]), _r(o.pts[0][1]), _r(o.pts[-1][0]), _r(o.pts[-1][1])], "ancho": _r(o.linewidth, 2)})
        elif isinstance(o, layout.LTRect):
            rects.append({"bbox": [_r(o.x0), _r(o.y0), _r(o.x1), _r(o.y1)], "relleno": bool(o.fill), "trazo": bool(o.stroke), "ancho": _r(o.linewidth, 2)})
        elif isinstance(o, layout.LTCurve):
            curves.append({"pts": [[_r(x), _r(y)] for (x, y) in o.pts], "bbox": [_r(o.x0), _r(o.y0), _r(o.x1), _r(o.y1)],
                           "relleno": bool(o.fill), "cerrada": _closed(o.pts), "ancho": _r(o.linewidth, 2)})
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


# ---------------- metadatos de lámina (extracción textual, no interpretación) ----------------

SHEET_RX = re.compile(r"^[A-Z]{1,2}-?\d+(?:\.\d+){0,3}[A-Z]?$")
REV_RX = re.compile(r"REV(?:ISION|\.)?\s*[:#]?\s*([A-Z]{0,4}\d+[A-Z0-9]*)", re.I)
SCALE_RX = re.compile(r"(\d+(?:/\d+)?)\"\s*=\s*1'-0\"")


def _sheet_meta(words, frame):
    """Candidatos de número de lámina, revisión y escala con el texto fuente y su posición. Confianza = regla, no comprensión."""
    meta = {"metodo": "extraccion_textual", "numero": None, "revision": None, "escala": None}
    big = sorted([w for w in words if SHEET_RX.match(w["texto"])], key=lambda w: -w["tam"])
    if big:
        meta["numero"] = {"valor": big[0]["texto"], "fuente": big[0]["bbox"], "confianza": 0.8 if big[0]["tam"] >= 12 else 0.5}
    joined = []
    # reconstruir líneas por altura para encontrar "REVISION: CCD7" y "SCALE: 1/8" = 1'-0""
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


# ---------------- trabajos ----------------


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
            "aviso": None if text_chars or vec else "sin texto ni vectores extraíbles: probablemente escaneado; requiere interpretación visual autorizada"}


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
        raise JobError("SIN_CONTENIDO", "no hay texto ni geometría extraíble en las páginas pedidas (¿escaneado?); "
                                        "requiere interpretación visual autorizada, no se inventa contenido")
    return {"sha256": sha256_file(path), "paginas": out_pages, "truncado": truncated, "clase": "extraccion_textual+geometrica",
            "nota": "Coordenadas en puntos del espacio visible de la página; no son coordenadas de Revit."}


def tables(args, paths):
    """Tablas con REGLAS (líneas o bordes de rectángulos que forman una rejilla). Celda = texto cuyo centro cae dentro."""
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
                                    "motivo": "filas de %.0f pt: rejilla de plano probable, no tabla" % row_h[len(row_h) // 2]}))
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
    """Agrupa líneas H/V que se cruzan en rejillas (≥2 filas y ≥2 columnas)."""
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
    Símbolos CANDIDATOS: formas cerradas pequeñas agrupadas por firma (tipo, tamaño, relleno). La leyenda (forma + "CÓDIGO - texto")
    da el significado de cada firma; un rótulo cercano lo confirma o lo contradice. Nada se clasifica sin leyenda o rótulo:
    lo dudoso queda en "ambiguos" para un agente autorizado. Es extracción geométrica + reglas, no visión.
    """
    path = paths.check(args.get("ruta"))
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
        _, rects, curves = _vectors(layout, pg)
        shapes = []
        for c in curves:
            if not c["cerrada"]:
                continue
            x0, y0, x1, y1 = c["bbox"]
            w, h = x1 - x0, y1 - y0
            if 0.5 < w <= max_pt and 0.5 < h <= max_pt:
                kind = "circulo" if abs(w - h) < 0.15 * max(w, h) and len(c["pts"]) >= 4 else "curva"
                shapes.append({"tipo": kind, "bbox": c["bbox"], "relleno": c["relleno"]})
        for r in rects:
            x0, y0, x1, y1 = r["bbox"]
            w, h = x1 - x0, y1 - y0
            if 0.5 < w <= max_pt * 2 and 0.5 < h <= max_pt and min(w, h) < max_pt:
                shapes.append({"tipo": "rectangulo", "bbox": r["bbox"], "relleno": r["relleno"]})
        for s in shapes:
            x0, y0, x1, y1 = s["bbox"]
            w, h = x1 - x0, y1 - y0
            s["centro"] = [_r((x0 + x1) / 2), _r((y0 + y1) / 2)]
            s["w"], s["h"] = _r(w, 2), _r(h, 2)
            s["firma"] = "%s|%s|%s|%s" % (s["tipo"], _q(max(w, h)), _q(min(w, h)), "lleno" if s["relleno"] else "hueco")
            s["rot_deg"] = 0.0 if w >= h else 90.0
        # leyenda: forma con texto a la derecha en la misma línea "COD - descripción"
        legend = {}
        legend_idx = set()
        for i, s in enumerate(shapes):
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
        for i, s in enumerate(shapes):
            if i in legend_idx:
                continue
            cx, cy = s["centro"]
            near = [w for w in words if math.hypot((w["bbox"][0] + w["bbox"][2]) / 2 - cx, (w["bbox"][1] + w["bbox"][3]) / 2 - cy) <= label_r]
            near.sort(key=lambda w: math.hypot((w["bbox"][0] + w["bbox"][2]) / 2 - cx, (w["bbox"][1] + w["bbox"][3]) / 2 - cy))
            label = near[0]["texto"] if near else None
            lg = legend.get(s["firma"])
            entry = {"centro_pt": s["centro"], "centro_norm": fr.norm(cx, cy), "centro_in": fr.inches(cx, cy), "firma": s["firma"],
                     "tamano_pt": [s["w"], s["h"]], "rot_deg": s["rot_deg"], "rotulo": label}
            if lg and not lg.get("conflicto"):
                entry["codigo"] = lg["codigo"]
                if label is None:
                    entry["confianza"] = 0.75; entry["motivo"] = "firma de leyenda sin rótulo cercano"
                elif label == lg["codigo"]:
                    entry["confianza"] = 0.95; entry["motivo"] = "firma de leyenda y rótulo coinciden"
                else:
                    entry["confianza"] = 0.4; entry["motivo"] = "rótulo «%s» contradice la leyenda «%s»" % (label, lg["codigo"])
                    amb.append(entry); continue
                cands.append(entry)
            elif label and any(l["codigo"] == label for l in legend.values()):
                entry["codigo"] = label; entry["confianza"] = 0.5; entry["motivo"] = "rótulo de leyenda con una firma distinta a la de la leyenda"
                amb.append(entry)
            else:
                unclassified.setdefault(s["firma"], []).append(entry)
        # 2.ª pasada: firma SIN leyenda cuyos rótulos coinciden de forma consistente (≥80 %, ≥3 formas) en un código
        # que la leyenda no asigna a otra firma -> candidato de confianza media; el resto del grupo, ambiguo.
        legend_codes = {v["codigo"] for v in legend.values()}
        rest = {}
        for firma, group in unclassified.items():
            labels = [e["rotulo"] for e in group if e["rotulo"]]
            top = max(set(labels), key=labels.count) if labels else None
            if top and top not in legend_codes and len(group) >= 3 and labels.count(top) >= 0.8 * len(group) and CODE_RX.match(top):
                for e in group:
                    if e["rotulo"] == top:
                        e["codigo"] = top; e["confianza"] = 0.7
                        e["motivo"] = "firma sin leyenda; rótulo «%s» consistente en %d/%d formas" % (top, labels.count(top), len(group))
                        cands.append(e)
                    else:
                        e["codigo"] = top; e["confianza"] = 0.3; e["motivo"] = "grupo rotulado «%s» pero esta forma no tiene ese rótulo" % top
                        amb.append(e)
            else:
                rest[firma] = len(group)
        unclassified = rest
        pages.append(fr.to_visible({"pagina": num, "marco": fr.json(), "leyenda": [dict(v, firma=k) for k, v in legend.items()], "candidatos": cands,
                      "ambiguos": amb, "sin_leyenda": [{"firma": k, "cantidad": v} for k, v in sorted(unclassified.items())],
                      "lamina": _sheet_meta(words, fr)}))
    return {"sha256": sha256_file(path), "paginas": pages, "clase": "extraccion_geometrica+reglas_de_leyenda",
            "nota": "Candidatos, no luminarias confirmadas. Las ambigüedades requieren un agente o una persona autorizados."}


def _q(v):
    return "%.1f" % (round(float(v) * 2) / 2)


GRID_LABEL_RX = re.compile(r"^([A-Z]{1,2}(\.\d{1,2})?|\d{1,3}(\.\d{1,2})?)$")


def grids(args, paths):
    """
    Ejes del plano: líneas largas con un globo (círculo con rótulo corto) en un extremo, e intersecciones entre ejes con
    nombre. Las intersecciones son ANCLAS candidatas para el registro plano–modelo (el modelo aporta las suyas por nombre
    de eje). Coordenadas en el espacio visible de la página; nunca son coordenadas de Revit.
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
        # Algunos exportadores dibujan el rótulo sin círculo. Esta heurística es optativa y
        # sólo se aplica en páginas sin globos reconocibles. Busca rótulos aislados, alineados
        # con el extremo de un trazo largo y situados inmediatamente fuera de él.
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
                    perp = abs((ex - ox) * (oy - cy) - (ox - cx) * (ey - oy)) / L   # distancia del centro a la recta
                    ahead = ((cx - ex) * (ex - ox) + (cy - ey) * (ey - oy)) / L     # el globo está más allá del extremo
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
                    continue   # casi paralelos: no dan un ancla fiable
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
            "nota": "Anclas candidatas por nombre de eje. El registro exige las mismas intersecciones en el modelo."}
