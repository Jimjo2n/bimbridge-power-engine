# SPDX-License-Identifier: GPL-3.0-only
"""Python Worker tests (contract pe=1) with SYNTHETIC fixtures. Run: python3 -m unittest -v tests/test_worker.py"""
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import zipfile
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
    """Real, isolated process (-I -B), speaking JSON lines the way the C# core does."""

    def __init__(self, paths=None):
        self.p = subprocess.Popen([sys.executable, "-I", "-B", os.path.join(ROOT, "run_worker.py")], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.paths = [FX] if paths is None else paths
        self.n = 0

    def raw(self, line):
        self.p.stdin.write((line + "\n").encode()); self.p.stdin.flush()
        return json.loads(self.p.stdout.readline())

    def call(self, job, args=None, limits=None, paths=None, pe=1):
        self.n += 1
        req = {"pe": pe, "id": "t%d" % self.n, "job": job, "args": args or {}, "paths": self.paths if paths is None else paths}
        if limits:
            req["limits"] = limits
        r = self.raw(json.dumps(req))
        assert r["id"] == req["id"], "broken correlation"
        return r

    def close(self):
        try:
            self.p.stderr.close()
            self.p.stdin.write(b'{"pe":1,"job":"exit"}\n'); self.p.stdin.flush()
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
        r = self.w.call("hello", {"pe_min": 1, "pe_max": 1})
        self.assertEqual(r["code"], "OK")
        self.assertEqual(r["result"]["pe"], 1)
        self.assertIn("pdf_symbols", r["result"]["capabilities"])

    def test_version_mismatch(self):
        self.assertEqual(self.w.call("hello", {"pe_min": 2, "pe_max": 3})["code"], "VERSION_INCOMPATIBLE")
        self.assertEqual(self.w.call("pdf_info", {"path": fx("plan_vectorial.pdf")}, pe=2)["code"], "VERSION_INCOMPATIBLE")

    def test_unknown_job_and_bad_json_do_not_kill_worker(self):
        self.assertEqual(self.w.call("format_disk")["code"], "NOT_SUPPORTED")
        self.assertEqual(self.w.raw("{not json")["code"], "INVALID")
        self.assertEqual(self.w.call("hello")["code"], "OK")

    def test_telemetry(self):
        r = self.w.call("pdf_info", {"path": fx("plan_vectorial.pdf")})
        self.assertGreater(r["telemetry"]["engine_ms"], 0)


class PdfDeliveryTests(unittest.TestCase):
    def test_current_issue_passes_and_previous_issue_reports_order_stamp_color_and_receipt(self):
        with tempfile.TemporaryDirectory(dir=FX) as folder:
            current = os.path.join(folder, "current.pdf")
            previous = os.path.join(folder, "previous.pdf")
            for path, revision, color in ((current, "QC REV 02", "1 0 1"), (previous, "QC REV 01", "0 0 0")):
                pdf = fixtures.Pdf()
                contents = [
                    "BT /F1 12 Tf 50 50 Td (E-101 " + revision + ") Tj ET\n" + color + " RG 60 70 m 120 70 l S",
                    "BT /F1 12 Tf 50 50 Td (E-102 " + revision + ") Tj ET",
                ]
                if path == previous:
                    contents.reverse()
                for page in contents:
                    pdf.page(612, 792, page)
                pdf.write(path)
            worker = Worker()
            try:
                receipt = worker.call("pdf_info", {"path": current})["result"]["sha256"]
                expected = [{"texts": ["E-101", "QC REV 02"], "paper_pt": [612, 792], "vector_rgb": [[255, 0, 255]]},
                            {"texts": ["E-102", "QC REV 02"]}]
                args = {"path": current, "previous": previous, "expected": expected, "expected_sha256": receipt}
                valid = worker.call("pdf_verify", args)
                self.assertEqual(valid["code"], "OK", valid)
                self.assertEqual(valid["result"]["status"], "verified", valid)
                stale = worker.call("pdf_verify", dict(args, path=previous))["result"]
                self.assertEqual(stale["status"], "with_differences")
                self.assertEqual({d["type"] for d in stale["differences"]},
                                 {"different_receipt", "same_as_previous", "missing_text", "vector_rgb_not_found"})
            finally:
                worker.close()


class FeederWorkbookTests(unittest.TestCase):
    def test_xlsx_schedule_reads_source_cells_and_explicit_open_terminal(self):
        with tempfile.TemporaryDirectory(dir=FX) as folder:
            path = os.path.join(folder, "feeders.xlsx")
            namespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
            relationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
            with zipfile.ZipFile(path, "w") as book:
                book.writestr("xl/workbook.xml",
                              f'<workbook xmlns="{namespace}" xmlns:r="{relationship}"><sheets><sheet name="Feeders" r:id="r1"/></sheets></workbook>')
                book.writestr("xl/_rels/workbook.xml.rels", '<Relationships><Relationship Id="r1" Target="worksheets/sheet1.xml"/></Relationships>')
                book.writestr("xl/sharedStrings.xml", f'<sst xmlns="{namespace}"><si><t>XF-2</t></si></sst>')
                book.writestr("xl/worksheets/sheet1.xml", f"""<worksheet xmlns="{namespace}"><sheetData>
                    <row r="1"><c r="A1" t="inlineStr"><is><t>ID</t></is></c><c r="B1" t="inlineStr"><is><t>Origen</t></is></c>
                    <c r="C1" t="inlineStr"><is><t>Destino</t></is></c><c r="D1" t="inlineStr"><is><t>Abierto</t></is></c></row>
                    <row r="2"><c r="A2" t="s"><v>0</v></c><c r="B2" t="inlineStr"><is><t>METER</t></is></c>
                    <c r="D2" t="b"><v>1</v></c></row>
                    <row r="3"><c r="A3" t="inlineStr"><is><t>NOTE</t></is></c></row>
                    </sheetData></worksheet>""")
            worker = Worker()
            try:
                args = {"path": path, "sheet": "Feeders"}
                self.assertEqual(worker.call("xlsx_feeders", args)["code"], "INVALID")
                response = worker.call("xlsx_feeders", dict(args, start_row=2, end_row=2))
                self.assertEqual(response["code"], "OK", response)
                edge, = response["result"]["edges"]
                self.assertEqual(edge["id"], "XF-2")
                self.assertIsNone(edge["target"])
                self.assertTrue(edge["open"])
                self.assertEqual(edge["source"]["cells"]["origin"], "B2")
                self.assertEqual(worker.call("xlsx_feeders", dict(args, start_row=3, end_row=2))["code"], "INVALID")
            finally:
                worker.close()


class PathAndNetworkGuards(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = Worker()
        cls.outside = tempfile.mkdtemp()
        shutil.copy(fx("plan_vectorial.pdf"), os.path.join(cls.outside, "outside.pdf"))

    @classmethod
    def tearDownClass(cls):
        cls.w.close()
        shutil.rmtree(cls.outside)

    def test_outside_path_denied(self):
        self.assertEqual(self.w.call("pdf_info", {"path": os.path.join(self.outside, "outside.pdf")})["code"], "PATH_DENIED")

    def test_verify_checks_authorization_for_previous_issue(self):
        reply = self.w.call("pdf_verify", {"path": fx("plan_vectorial.pdf"), "previous": os.path.join(self.outside, "outside.pdf"),
                                           "expected": [{"texts": ["E-101"]}]})
        self.assertEqual(reply["code"], "PATH_DENIED")

    def test_dotdot_denied(self):
        p = os.path.join(FX, "..", os.path.basename(self.outside), "outside.pdf")
        self.assertEqual(self.w.call("pdf_info", {"path": p})["code"], "PATH_DENIED")

    def test_symlink_escape_denied(self):
        with tempfile.TemporaryDirectory(dir=FX) as folder:
            link = os.path.join(folder, "outside_link.pdf")
            try:
                os.symlink(os.path.join(self.outside, "outside.pdf"), link)
            except OSError as exc:
                if getattr(exc, "winerror", None) == 1314:
                    self.skipTest("Windows requires the symbolic link privilege")
                raise
            self.assertEqual(self.w.call("pdf_info", {"path": link})["code"], "PATH_DENIED")

    def test_no_authorized_paths_means_nothing(self):
        self.assertEqual(self.w.call("pdf_info", {"path": fx("plan_vectorial.pdf")}, paths=[])["code"], "PATH_DENIED")

    def test_prefix_sibling_denied(self):
        # /x/fx does not authorize /x/fx_other
        sib = FX.rstrip(os.sep) + "_other"
        os.makedirs(sib, exist_ok=True)
        shutil.copy(fx("plan_vectorial.pdf"), os.path.join(sib, "a.pdf"))
        try:
            self.assertEqual(self.w.call("pdf_info", {"path": os.path.join(sib, "a.pdf")})["code"], "PATH_DENIED")
        finally:
            shutil.rmtree(sib)

    def test_network_blocked_in_process(self):
        code = ("import sys,socket; sys.path.insert(0,%r); from bimbridge_docworker import worker as w; w.block_network()\n"
                "try:\n socket.create_connection(('127.0.0.1',9)); print('NETWORK')\nexcept w.JobError as e: print(e.code)") % ROOT
        out = subprocess.run([sys.executable, "-I", "-c", code], capture_output=True, text=True).stdout.strip()
        self.assertEqual(out, "PATH_DENIED")


class PdfTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = Worker()

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def _symbols_match_truth(self, name):
        r = self.w.call("pdf_symbols", {"path": fx(name)})
        self.assertEqual(r["code"], "OK", r.get("error"))
        page = r["result"]["pages"][0]
        cands = page["candidates"]
        t = truth(name)
        for lum in t["fixtures"]:
            x, y = lum["sheet_pt"]
            best = min(cands, key=lambda c: math.hypot(c["center_pt"][0] - x, c["center_pt"][1] - y))
            self.assertLess(math.hypot(best["center_pt"][0] - x, best["center_pt"][1] - y), 0.6, "%s %s missed" % (name, lum))
            self.assertEqual(best["code"], lum["code"])
        # the near-duplicates of the plan are also extracted (they are resolved in registration/planning, not here)
        self.assertEqual(len(cands), len(t["fixtures"]) + len(t["near_duplicates"]))
        self.assertEqual(page["sheet"]["number"]["value"], "E-1.3.09")
        self.assertEqual(page["sheet"]["revision"]["value"], "CCD7")
        self.assertEqual(page["sheet"]["scale"]["revit_factor"], 96.0)
        return page

    def test_symbols_vector(self):
        p = self._symbols_match_truth("plan_vectorial.pdf")
        self.assertEqual({l["code"] for l in p["legend"]}, {"D", "G", "S"})

    def test_identical_overprints_preserve_the_first_id_and_accept_an_old_alias(self):
        from hashlib import sha256

        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "overprint.pdf")
            c = fixtures.Canvas()
            c.rect(20, 30, 10, 6)
            c.rect(20, 30, 10, 6)
            c.rect(21, 30, 10, 6)
            pdf = fixtures.Pdf()
            pdf.page(200, 100, "\n".join(c.ops))
            pdf.write(path)
            args = {"path": path, "traceability": True}
            result = self.w.call("pdf_symbols", args, paths=[folder])["result"]
            inventory = result["pages"][0]["inventory"]
            self.assertEqual([s["center_pt"] for s in inventory], [[25, 33], [26, 33]])
            raw_alias = "%s:1:1:rectangulo|10.0|6.0|hueco:[25.0, 33.0]" % result["sha256"]
            alias = sha256(raw_alias.encode()).hexdigest()[:24]
            alias_decision = {"symbol_id": alias, "code": "LT", "decision": "include", "evidence": "review"}
            applied = self.w.call("pdf_symbols", dict(args, decisions=[alias_decision]), paths=[folder])
            self.assertEqual(applied["code"], "OK", applied.get("error"))
            confirmed = applied["result"]["pages"][0]["committed"]
            self.assertEqual([entry["symbol_id"] for entry in confirmed], [inventory[0]["symbol_id"]])
            conflict = {"symbol_id": inventory[0]["symbol_id"], "decision": "exclude", "evidence": "review"}
            rejected = self.w.call("pdf_symbols", dict(args, decisions=[alias_decision, conflict]), paths=[folder])
            self.assertEqual(rejected["code"], "INVALID")

    def test_symbols_page_rotate90_visible_space(self):
        p = self._symbols_match_truth("plan_rotado.pdf")
        self.assertEqual((p["frame"]["width_pt"], p["frame"]["height_pt"], p["frame"]["rotation"]), (1728.0, 2592.0, 90))
        self.assertEqual(len(p["legend"]), 3, "the legend is read even when the page is rotated")

    def test_symbols_model_rotated30(self):
        self._symbols_match_truth("plan_girado30.pdf")

    def test_confidence_only_high_when_legend_and_label_agree(self):
        page = self.w.call("pdf_symbols", {"path": fx("plan_vectorial.pdf")})["result"]["pages"][0]
        self.assertTrue(all(c["confidence"] == 0.95 for c in page["candidates"]))
        self.assertTrue(all(a["confidence"] < 0.7 for a in page["ambiguous_items"]))

    def test_grids_and_anchor_intersections_all_variants(self):
        for name in ("plan_vectorial.pdf", "plan_rotado.pdf", "plan_girado30.pdf"):
            r = self.w.call("pdf_grids", {"path": fx(name)})
            self.assertEqual(r["code"], "OK", r.get("error"))
            p = r["result"]["pages"][0]
            self.assertEqual(sorted(g["axis"] for g in p["grids"]), ["1", "2", "3", "4", "5", "A", "B", "C", "D", "E", "F"])
            self.assertEqual(len(p["intersections"]), 30)
            got = {x["name"]: x["point_pt"] for x in p["intersections"]}
            for a in truth(name)["anchors"]:
                pt = got[a["axis"][0] + "/" + a["axis"][1:]]
                self.assertAlmostEqual(pt[0], a["sheet_pt"][0], delta=0.01)
                self.assertAlmostEqual(pt[1], a["sheet_pt"][1], delta=0.01)

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
            args = {"path": path}
            strict = self.w.call("pdf_grids", args, paths=[folder])
            self.assertEqual(strict["result"]["pages"][0]["grids"], [])
            inferred = self.w.call("pdf_grids", {**args, "no_bubble": True}, paths=[folder])
            page = inferred["result"]["pages"][0]
            self.assertEqual(page["grids_method"], "labels_without_bubble")
            self.assertEqual(sorted(g["axis"] for g in page["grids"]), ["1", "2", "A", "B"])
            self.assertEqual(sorted(a["name"] for a in page["intersections"]), ["A/1", "A/2", "B/1", "B/2"])

    def test_tables_schedule_and_grid_rejected(self):
        r = self.w.call("pdf_tables", {"path": fx("plan_vectorial.pdf")})["result"]
        self.assertEqual(len(r["tables"]), 1)
        t = r["tables"][0]
        self.assertEqual(t["cells"][0], ["TYPE", "DESCRIPTION", "WATTS", "MOUNTING"])
        self.assertIn(["S", "BS100LED-4-SA-HO", "70", "WALL"], t["cells"])
        self.assertEqual(t["title"], "LUMINAIRE SCHEDULE")
        self.assertEqual(len(r["discarded"]), 1)
        r2 = self.w.call("pdf_tables", {"path": fx("plan_vectorial.pdf"), "include_grids": True})["result"]
        self.assertEqual(len(r2["tables"]), 2)

    def test_tables_rotated_page(self):
        r = self.w.call("pdf_tables", {"path": fx("plan_rotado.pdf")})["result"]
        self.assertEqual(r["tables"][0]["cells"][0], ["TYPE", "DESCRIPTION", "WATTS", "MOUNTING"])

    def test_multipage_sizes_and_selection(self):
        r = self.w.call("pdf_info", {"path": fx("multipagina.pdf")})["result"]
        self.assertEqual(r["pages"], 3)
        sizes = [(p["width_in"], p["height_in"]) for p in r["pages_detail"]]
        self.assertEqual(sizes[0], (8.5, 11.0))
        self.assertEqual(len(set(sizes)), 3)
        e = self.w.call("pdf_extract", {"path": fx("multipagina.pdf"), "pages": [2]})
        self.assertEqual([p["number"] for p in e["result"]["pages"]], [2])
        self.assertEqual(self.w.call("pdf_extract", {"path": fx("multipagina.pdf"), "pages": [4]})["code"], "INVALID")

    def test_userunit(self):
        p = self.w.call("pdf_info", {"path": fx("userunit2.pdf")})["result"]["pages_detail"][0]
        self.assertEqual((p["user_unit"], p["width_in"], p["height_in"]), (2.0, 8.5, 11.0))

    def test_extract_coordinates_and_truncation(self):
        e = self.w.call("pdf_extract", {"path": fx("plan_vectorial.pdf")})["result"]
        pg = e["pages"][0]
        self.assertFalse(e["truncated"])
        self.assertTrue(any(w["text"] == "E-1.3.09" for w in pg["words"]))
        self.assertGreater(len(pg["lines"]) + len(pg["rectangles"]) + len(pg["curves"]), 100)
        for w in pg["words"]:
            self.assertTrue(0 <= w["center_norm"][0] <= 1 and 0 <= w["center_norm"][1] <= 1)
        t = self.w.call("pdf_extract", {"path": fx("multipagina.pdf"), "max_elements": 5})["result"]
        self.assertTrue(t["truncated"])

    def test_extract_rotated_is_visible_space(self):
        e = self.w.call("pdf_extract", {"path": fx("plan_rotado.pdf"), "include": ["text"]})["result"]["pages"][0]
        for w in e["words"]:
            self.assertTrue(0 <= w["bbox"][0] <= 1728.01 and 0 <= w["bbox"][1] <= 2592.01, w)

    def test_scanned_has_no_content(self):
        self.assertEqual(self.w.call("pdf_extract", {"path": fx("escaneado.pdf")})["code"], "NO_CONTENT")
        i = self.w.call("pdf_info", {"path": fx("escaneado.pdf")})["result"]
        self.assertTrue(i["pages_detail"][0]["image"])
        self.assertIsNotNone(i["warning"])

    def test_corrupt(self):
        self.assertEqual(self.w.call("pdf_info", {"path": fx("danado.pdf")})["code"], "INVALID")
        self.assertEqual(self.w.call("pdf_symbols", {"path": fx("danado.pdf")})["code"], "INVALID")

    def test_docx_given_to_pdf_job(self):
        self.assertEqual(self.w.call("pdf_info", {"path": fx("especificacion.docx")})["code"], "INVALID")


class DocxTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = Worker()

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def test_docx_structure_and_injection_is_data(self):
        r = self.w.call("docx_extract", {"path": fx("especificacion.docx")})
        self.assertEqual(r["code"], "OK")
        res = r["result"]
        self.assertEqual(res["blocks"][0]["style"], "Heading1")
        self.assertGreaterEqual(res["tables"], 1)
        tab = [b for b in res["blocks"] if b["type"] == "table"][0]
        self.assertGreaterEqual(len(tab["rows"]), 2)
        self.assertEqual(len(res["suspects"]), 1)

    def test_docx_invalid(self):
        self.assertEqual(self.w.call("docx_extract", {"path": fx("plan_vectorial.pdf")})["code"], "INVALID")

    def test_docx_xxe_rejected(self):
        import zipfile
        p = fx("xxe.docx")
        with zipfile.ZipFile(p, "w") as z:
            z.writestr("word/document.xml", '<?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]>'
                       '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>')
        try:
            self.assertEqual(self.w.call("docx_extract", {"path": p})["code"], "INVALID")
        finally:
            os.unlink(p)


class LargeAndLimits(unittest.TestCase):
    def test_large_document(self):
        w = Worker()
        try:
            t0 = time.perf_counter()
            r = w.call("pdf_symbols", {"path": fx("grande_40p.pdf")})
            dt = time.perf_counter() - t0
            self.assertEqual(r["code"], "OK")
            pages = r["result"]["pages"]
            self.assertEqual(len(pages), 40)
            bubble = "circulo|14.0|14.0|hueco"  # grid bubbles: they are not fixtures
            n = sum(1 for p in pages for c in p["candidates"] + p["ambiguous_items"] if c["signature"] != bubble)
            n += sum(s["quantity"] for p in pages for s in p["no_legend"] if s["signature"] != bubble)
            self.assertEqual(n, 20000, "every fixture ends up in candidatos, ambiguos or no_legend; none is lost")
            self.assertGreater(sum(len(p["candidates"]) for p in pages), 19800)
            self.assertLess(dt, 60.0)
            sys.stderr.write("\n[grande_40p] pdf_symbols %.1f s, peak %s kB\n" % (dt, r["telemetry"].get("peak_memory_kb")))
        finally:
            w.close()

    @unittest.skipUnless(sys.platform.startswith("linux"), "RLIMIT_AS is POSIX only; on Windows the C# core enforces it")
    def test_memory_limit_reported(self):
        w = Worker()
        try:
            r = w.call("pdf_symbols", {"path": fx("grande_40p.pdf")}, limits={"max_mem_mb": 120})
            self.assertIn(r["code"], ("MEMORY_LIMIT", "OK"))
        finally:
            w.p.kill(); w.close()

    def test_kill_mid_job_then_restart(self):
        w = Worker()
        req = {"pe": 1, "id": "x", "job": "pdf_symbols", "args": {"path": fx("grande_40p.pdf")}, "paths": [FX]}
        w.p.stdin.write((json.dumps(req) + "\n").encode()); w.p.stdin.flush()
        time.sleep(0.5)
        w.p.kill(); w.close()  # cancellation from C# means killing the process; no shared state remains
        w2 = Worker()
        try:
            self.assertEqual(w2.call("hello")["code"], "OK")
        finally:
            w2.close()


if __name__ == "__main__":
    unittest.main()
