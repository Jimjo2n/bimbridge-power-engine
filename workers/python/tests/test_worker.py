# SPDX-License-Identifier: GPL-3.0-only
"""Pruebas del Python Worker (contrato pe=1) con fixtures SINTÉTICOS. Ejecutar: python3 -m unittest -v tests/test_worker.py"""
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
sys.path.insert(0, HERE)
import fixtures  # noqa: E402

FX = os.environ.get("BIMBRIDGE_FX") or os.path.join(tempfile.gettempdir(), "bimbridge_fx_%d" % os.getpid())


def setUpModule():
    if not os.path.isfile(os.path.join(FX, "truth.json")):
        fixtures.build(FX)


class Worker:
    """Proceso real, aislado (-I -B), hablando JSON por líneas como lo hace el núcleo C#."""

    def __init__(self, rutas=None):
        self.p = subprocess.Popen([sys.executable, "-I", "-B", os.path.join(ROOT, "run_worker.py")], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.rutas = [FX] if rutas is None else rutas
        self.n = 0

    def raw(self, line):
        self.p.stdin.write((line + "\n").encode()); self.p.stdin.flush()
        return json.loads(self.p.stdout.readline())

    def call(self, job, args=None, limites=None, rutas=None, pe=1):
        self.n += 1
        req = {"pe": pe, "id": "t%d" % self.n, "trabajo": job, "args": args or {}, "rutas": self.rutas if rutas is None else rutas}
        if limites:
            req["limites"] = limites
        r = self.raw(json.dumps(req))
        assert r["id"] == req["id"], "correlación rota"
        return r

    def close(self):
        try:
            self.p.stderr.close()
            self.p.stdin.write(b'{"pe":1,"trabajo":"salir"}\n'); self.p.stdin.flush()
            self.p.wait(5)
        except Exception:  # noqa: BLE001
            self.p.kill()
        for f in (self.p.stdin, self.p.stdout):
            try:
                f.close()
            except Exception:  # noqa: BLE001
                pass


def fx(name):
    return os.path.join(FX, name)


def truth(name):
    with open(fx("truth.json")) as f:
        return json.load(f)[name]


class ContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = Worker()

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def test_handshake(self):
        r = self.w.call("hola", {"pe_min": 1, "pe_max": 1})
        self.assertEqual(r["codigo"], "OK")
        self.assertEqual(r["resultado"]["pe"], 1)
        self.assertIn("pdf_symbols", r["resultado"]["capacidades"])

    def test_version_mismatch(self):
        self.assertEqual(self.w.call("hola", {"pe_min": 2, "pe_max": 3})["codigo"], "VERSION_INCOMPATIBLE")
        self.assertEqual(self.w.call("pdf_info", {"ruta": fx("plan_vectorial.pdf")}, pe=2)["codigo"], "VERSION_INCOMPATIBLE")

    def test_unknown_job_and_bad_json_do_not_kill_worker(self):
        self.assertEqual(self.w.call("formatear_disco")["codigo"], "NO_SOPORTADO")
        self.assertEqual(self.w.raw("{esto no es json")["codigo"], "INVALIDO")
        self.assertEqual(self.w.call("hola")["codigo"], "OK")

    def test_telemetry(self):
        r = self.w.call("pdf_info", {"ruta": fx("plan_vectorial.pdf")})
        self.assertGreater(r["telemetria"]["motor_ms"], 0)


class PathAndNetworkGuards(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = Worker()
        cls.outside = tempfile.mkdtemp()
        shutil.copy(fx("plan_vectorial.pdf"), os.path.join(cls.outside, "fuera.pdf"))

    @classmethod
    def tearDownClass(cls):
        cls.w.close()
        shutil.rmtree(cls.outside)

    def test_outside_path_denied(self):
        self.assertEqual(self.w.call("pdf_info", {"ruta": os.path.join(self.outside, "fuera.pdf")})["codigo"], "RUTA_DENEGADA")

    def test_dotdot_denied(self):
        p = os.path.join(FX, "..", os.path.basename(self.outside), "fuera.pdf")
        self.assertEqual(self.w.call("pdf_info", {"ruta": p})["codigo"], "RUTA_DENEGADA")

    def test_symlink_escape_denied(self):
        with tempfile.TemporaryDirectory(dir=FX) as folder:
            link = os.path.join(folder, "enlace_fuera.pdf")
            try:
                os.symlink(os.path.join(self.outside, "fuera.pdf"), link)
            except OSError as exc:
                if getattr(exc, "winerror", None) == 1314:
                    self.skipTest("Windows requiere privilegio de enlace simbólico")
                raise
            self.assertEqual(self.w.call("pdf_info", {"ruta": link})["codigo"], "RUTA_DENEGADA")

    def test_no_authorized_paths_means_nothing(self):
        self.assertEqual(self.w.call("pdf_info", {"ruta": fx("plan_vectorial.pdf")}, rutas=[])["codigo"], "RUTA_DENEGADA")

    def test_prefix_sibling_denied(self):
        # /x/fx no autoriza /x/fx_otro
        sib = FX.rstrip(os.sep) + "_otro"
        os.makedirs(sib, exist_ok=True)
        shutil.copy(fx("plan_vectorial.pdf"), os.path.join(sib, "a.pdf"))
        try:
            self.assertEqual(self.w.call("pdf_info", {"ruta": os.path.join(sib, "a.pdf")})["codigo"], "RUTA_DENEGADA")
        finally:
            shutil.rmtree(sib)

    def test_network_blocked_in_process(self):
        code = ("import sys,socket; sys.path.insert(0,%r); from bimbridge_docworker import worker as w; w.block_network()\n"
                "try:\n socket.create_connection(('127.0.0.1',9)); print('RED')\nexcept w.JobError as e: print(e.code)") % ROOT
        out = subprocess.run([sys.executable, "-I", "-c", code], capture_output=True, text=True).stdout.strip()
        self.assertEqual(out, "RUTA_DENEGADA")


class PdfTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = Worker()

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def _symbols_match_truth(self, name):
        r = self.w.call("pdf_symbols", {"ruta": fx(name)})
        self.assertEqual(r["codigo"], "OK", r.get("error"))
        page = r["resultado"]["paginas"][0]
        cands = page["candidatos"]
        t = truth(name)
        for lum in t["luminarias"]:
            x, y = lum["lamina_pt"]
            best = min(cands, key=lambda c: math.hypot(c["centro_pt"][0] - x, c["centro_pt"][1] - y))
            self.assertLess(math.hypot(best["centro_pt"][0] - x, best["centro_pt"][1] - y), 0.6, "%s %s omitido" % (name, lum))
            self.assertEqual(best["codigo"], lum["codigo"])
        # los casi duplicados del plano también se extraen (se resuelven en registro/planificación, no aquí)
        self.assertEqual(len(cands), len(t["luminarias"]) + len(t["casi_duplicados"]))
        self.assertEqual(page["lamina"]["numero"]["valor"], "E-1.3.09")
        self.assertEqual(page["lamina"]["revision"]["valor"], "CCD7")
        self.assertEqual(page["lamina"]["escala"]["factor_revit"], 96.0)
        return page

    def test_symbols_vector(self):
        p = self._symbols_match_truth("plan_vectorial.pdf")
        self.assertEqual({l["codigo"] for l in p["leyenda"]}, {"D", "G", "S"})

    def test_symbols_page_rotate90_visible_space(self):
        p = self._symbols_match_truth("plan_rotado.pdf")
        self.assertEqual((p["marco"]["ancho_pt"], p["marco"]["alto_pt"], p["marco"]["rotacion"]), (1728.0, 2592.0, 90))
        self.assertEqual(len(p["leyenda"]), 3, "la leyenda se lee aunque la página esté girada")

    def test_symbols_model_rotated30(self):
        self._symbols_match_truth("plan_girado30.pdf")

    def test_confidence_only_high_when_legend_and_label_agree(self):
        page = self.w.call("pdf_symbols", {"ruta": fx("plan_vectorial.pdf")})["resultado"]["paginas"][0]
        self.assertTrue(all(c["confianza"] == 0.95 for c in page["candidatos"]))
        self.assertTrue(all(a["confianza"] < 0.7 for a in page["ambiguos"]))

    def test_grids_and_anchor_intersections_all_variants(self):
        for name in ("plan_vectorial.pdf", "plan_rotado.pdf", "plan_girado30.pdf"):
            r = self.w.call("pdf_grids", {"ruta": fx(name)})
            self.assertEqual(r["codigo"], "OK", r.get("error"))
            p = r["resultado"]["paginas"][0]
            self.assertEqual(sorted(g["eje"] for g in p["ejes"]), ["1", "2", "3", "4", "5", "A", "B", "C", "D", "E", "F"])
            self.assertEqual(len(p["intersecciones"]), 30)
            got = {x["nombre"]: x["punto_pt"] for x in p["intersecciones"]}
            for a in truth(name)["anclas"]:
                pt = got[a["eje"][0] + "/" + a["eje"][1:]]
                self.assertAlmostEqual(pt[0], a["lamina_pt"][0], delta=0.01)
                self.assertAlmostEqual(pt[1], a["lamina_pt"][1], delta=0.01)

    def test_unbubbled_grids_require_opt_in_and_crossings(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "unbubbled.pdf")
            c = fixtures.Canvas()
            c.line(100, 60, 100, 295)
            c.line(360, 60, 360, 295)
            c.line(60, 95, 410, 95)
            c.line(60, 265, 410, 265)
            c.line(450, 60, 450, 295)
            c.ops.append("BT /F1 12 Tf 96 300 Td (A) Tj ET")
            c.ops.append("BT /F1 12 Tf 356 300 Td (B) Tj ET")
            c.ops.append("BT /F1 12 Tf 45 92 Td (1) Tj ET")
            c.ops.append("BT /F1 12 Tf 45 262 Td (2) Tj ET")
            c.ops.append("BT /F1 12 Tf 446 300 Td (C) Tj ET")
            pdf = fixtures.Pdf()
            pdf.page(480, 360, "\n".join(c.ops))
            pdf.write(path)
            args = {"ruta": path}
            strict = self.w.call("pdf_grids", args, rutas=[folder])
            self.assertEqual(strict["resultado"]["paginas"][0]["ejes"], [])
            inferred = self.w.call("pdf_grids", {**args, "sin_globo": True}, rutas=[folder])
            page = inferred["resultado"]["paginas"][0]
            self.assertEqual(page["metodo_ejes"], "rotulos_sin_globo")
            self.assertEqual(sorted(g["eje"] for g in page["ejes"]), ["1", "2", "A", "B"])
            self.assertEqual(sorted(a["nombre"] for a in page["intersecciones"]), ["A/1", "A/2", "B/1", "B/2"])

    def test_tables_schedule_and_grid_rejected(self):
        r = self.w.call("pdf_tables", {"ruta": fx("plan_vectorial.pdf")})["resultado"]
        self.assertEqual(len(r["tablas"]), 1)
        t = r["tablas"][0]
        self.assertEqual(t["celdas"][0], ["TYPE", "DESCRIPTION", "WATTS", "MOUNTING"])
        self.assertIn(["S", "BS100LED-4-SA-HO", "70", "WALL"], t["celdas"])
        self.assertEqual(t["titulo"], "LUMINAIRE SCHEDULE")
        self.assertEqual(len(r["descartadas"]), 1)
        r2 = self.w.call("pdf_tables", {"ruta": fx("plan_vectorial.pdf"), "incluir_rejillas": True})["resultado"]
        self.assertEqual(len(r2["tablas"]), 2)

    def test_tables_rotated_page(self):
        r = self.w.call("pdf_tables", {"ruta": fx("plan_rotado.pdf")})["resultado"]
        self.assertEqual(r["tablas"][0]["celdas"][0], ["TYPE", "DESCRIPTION", "WATTS", "MOUNTING"])

    def test_multipage_sizes_and_selection(self):
        r = self.w.call("pdf_info", {"ruta": fx("multipagina.pdf")})["resultado"]
        self.assertEqual(r["paginas"], 3)
        sizes = [(p["ancho_in"], p["alto_in"]) for p in r["detalle_paginas"]]
        self.assertEqual(sizes[0], (8.5, 11.0))
        self.assertEqual(len(set(sizes)), 3)
        e = self.w.call("pdf_extract", {"ruta": fx("multipagina.pdf"), "paginas": [2]})
        self.assertEqual([p["numero"] for p in e["resultado"]["paginas"]], [2])
        self.assertEqual(self.w.call("pdf_extract", {"ruta": fx("multipagina.pdf"), "paginas": [4]})["codigo"], "INVALIDO")

    def test_userunit(self):
        p = self.w.call("pdf_info", {"ruta": fx("userunit2.pdf")})["resultado"]["detalle_paginas"][0]
        self.assertEqual((p["unidad_usuario"], p["ancho_in"], p["alto_in"]), (2.0, 8.5, 11.0))

    def test_extract_coordinates_and_truncation(self):
        e = self.w.call("pdf_extract", {"ruta": fx("plan_vectorial.pdf")})["resultado"]
        pg = e["paginas"][0]
        self.assertFalse(e["truncado"])
        self.assertTrue(any(w["texto"] == "E-1.3.09" for w in pg["palabras"]))
        self.assertGreater(len(pg["lineas"]) + len(pg["rectangulos"]) + len(pg["curvas"]), 100)
        for w in pg["palabras"]:
            self.assertTrue(0 <= w["centro_norm"][0] <= 1 and 0 <= w["centro_norm"][1] <= 1)
        t = self.w.call("pdf_extract", {"ruta": fx("multipagina.pdf"), "max_elementos": 5})["resultado"]
        self.assertTrue(t["truncado"])

    def test_extract_rotated_is_visible_space(self):
        e = self.w.call("pdf_extract", {"ruta": fx("plan_rotado.pdf"), "incluir": ["texto"]})["resultado"]["paginas"][0]
        for w in e["palabras"]:
            self.assertTrue(0 <= w["bbox"][0] <= 1728.01 and 0 <= w["bbox"][1] <= 2592.01, w)

    def test_scanned_has_no_content(self):
        self.assertEqual(self.w.call("pdf_extract", {"ruta": fx("escaneado.pdf")})["codigo"], "SIN_CONTENIDO")
        i = self.w.call("pdf_info", {"ruta": fx("escaneado.pdf")})["resultado"]
        self.assertTrue(i["detalle_paginas"][0]["imagen"])
        self.assertIsNotNone(i["aviso"])

    def test_corrupt(self):
        self.assertEqual(self.w.call("pdf_info", {"ruta": fx("danado.pdf")})["codigo"], "INVALIDO")
        self.assertEqual(self.w.call("pdf_symbols", {"ruta": fx("danado.pdf")})["codigo"], "INVALIDO")

    def test_docx_given_to_pdf_job(self):
        self.assertEqual(self.w.call("pdf_info", {"ruta": fx("especificacion.docx")})["codigo"], "INVALIDO")


class DocxTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = Worker()

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def test_docx_structure_and_injection_is_data(self):
        r = self.w.call("docx_extract", {"ruta": fx("especificacion.docx")})
        self.assertEqual(r["codigo"], "OK")
        res = r["resultado"]
        self.assertEqual(res["bloques"][0]["estilo"], "Heading1")
        self.assertGreaterEqual(res["tablas"], 1)
        tab = [b for b in res["bloques"] if b["tipo"] == "tabla"][0]
        self.assertGreaterEqual(len(tab["filas"]), 2)
        self.assertEqual(len(res["sospechosos"]), 1)

    def test_docx_invalid(self):
        self.assertEqual(self.w.call("docx_extract", {"ruta": fx("plan_vectorial.pdf")})["codigo"], "INVALIDO")

    def test_docx_xxe_rejected(self):
        import zipfile
        p = fx("xxe.docx")
        with zipfile.ZipFile(p, "w") as z:
            z.writestr("word/document.xml", '<?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]>'
                       '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>')
        try:
            self.assertEqual(self.w.call("docx_extract", {"ruta": p})["codigo"], "INVALIDO")
        finally:
            os.unlink(p)


class LargeAndLimits(unittest.TestCase):
    def test_large_document(self):
        w = Worker()
        try:
            t0 = time.perf_counter()
            r = w.call("pdf_symbols", {"ruta": fx("grande_40p.pdf")})
            dt = time.perf_counter() - t0
            self.assertEqual(r["codigo"], "OK")
            pages = r["resultado"]["paginas"]
            self.assertEqual(len(pages), 40)
            bubble = "circulo|14.0|14.0|hueco"  # globos de ejes: no son luminarias
            n = sum(1 for p in pages for c in p["candidatos"] + p["ambiguos"] if c["firma"] != bubble)
            n += sum(s["cantidad"] for p in pages for s in p["sin_leyenda"] if s["firma"] != bubble)
            self.assertEqual(n, 20000, "toda luminaria acaba en candidatos, ambiguos o sin_leyenda; ninguna se pierde")
            self.assertGreater(sum(len(p["candidatos"]) for p in pages), 19800)
            self.assertLess(dt, 60.0)
            sys.stderr.write("\n[grande_40p] pdf_symbols %.1f s, pico %s kB\n" % (dt, r["telemetria"].get("memoria_pico_kb")))
        finally:
            w.close()

    @unittest.skipUnless(sys.platform.startswith("linux"), "RLIMIT_AS sólo POSIX; en Windows lo aplica el núcleo C#")
    def test_memory_limit_reported(self):
        w = Worker()
        try:
            r = w.call("pdf_symbols", {"ruta": fx("grande_40p.pdf")}, limites={"max_mem_mb": 120})
            self.assertIn(r["codigo"], ("LIMITE_MEMORIA", "OK"))
        finally:
            w.p.kill(); w.close()

    def test_kill_mid_job_then_restart(self):
        w = Worker()
        req = {"pe": 1, "id": "x", "trabajo": "pdf_symbols", "args": {"ruta": fx("grande_40p.pdf")}, "rutas": [FX]}
        w.p.stdin.write((json.dumps(req) + "\n").encode()); w.p.stdin.flush()
        time.sleep(0.5)
        w.p.kill(); w.close()  # la cancelación desde C# es matar el proceso; no queda estado compartido
        w2 = Worker()
        try:
            self.assertEqual(w2.call("hola")["codigo"], "OK")
        finally:
            w2.close()


if __name__ == "__main__":
    unittest.main()
