// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using static BimBridge.PowerEngine.PowerEngineContracts;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// docx_extract in .NET: same result as the Python Worker (blocks, styles, tables, suspicious phrases) to
    /// benchmark C# against Python and to degrade without Python. XML without DTD (XXE/expansion rejected). The text is DATA.
    /// </summary>
    public static class DocxExtractor
    {
        private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const long MaxXml = 64L * 1024 * 1024;
        // Same expression as bimbridge_docworker/docx.py (SUSPECT_RX); Spanish words are intentional detection data
        private static readonly Regex Suspect = new Regex(
            @"\b(ignore|ignora|olvida|disregard)\b.{0,40}\b(instruc|previous|anteriores|rules|rules)|\b(ejecuta|execute|run|borra|delete|elimina)\b.{0,30}\b(script|comando|command|model|model|archivo|file)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static Response Run(string file, Limits limits, CancellationToken ct, int maxItems = 200000)
        {
            var sw = Stopwatch.StartNew();
            var id = NewId();
            try
            {
                if (string.IsNullOrEmpty(file)) return Response.Fail(id, Codes.Invalid, "document path is missing");
                if (!File.Exists(file)) return Response.Fail(id, Codes.Invalid, "file does not exist: " + file);
                var r = new Response { Id = id, Result = Extract(file, ct, maxItems) };
                r.Telemetry.EngineMs = sw.Elapsed.TotalMilliseconds;
                return r;
            }
            catch (OperationCanceledException) { return Response.Fail(id, Codes.Cancelled, "cancelled"); }
            catch (DocError e) { return Response.Fail(id, e.Code, e.Message); }
            catch (InvalidDataException e) { return Response.Fail(id, Codes.Invalid, "not a valid DOCX (unreadable ZIP): " + e.Message); }
            catch (XmlException e) { return Response.Fail(id, Codes.Invalid, "unreadable XML or XML with DTD: " + e.Message); }
            catch (Exception e) { return Response.Fail(id, Codes.EngineError, e.GetType().Name + ": " + e.Message); }
            finally { sw.Stop(); }
        }

        private sealed class DocError : Exception
        {
            public readonly string Code;
            public DocError(string code, string msg) : base(msg) { Code = code; }
        }

        private static Dictionary<string, object> Extract(string file, CancellationToken ct, int maxItems)
        {
            byte[] xml;
            using (var z = ZipFile.OpenRead(file))
            {
                var e = z.GetEntry("word/document.xml") ?? throw new DocError(Codes.Invalid, "DOCX without word/document.xml");
                if (e.Length > MaxXml) throw new DocError(Codes.TooLarge, "document.xml exceeds " + (MaxXml >> 20) + " MB");
                using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); xml = ms.ToArray(); }
            }
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            var doc = new XmlDocument { XmlResolver = null };
            using (var rd = XmlReader.Create(new MemoryStream(xml), settings)) doc.Load(rd);
            var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("w", W);
            var body = doc.SelectSingleNode("/w:document/w:body", ns) ?? throw new DocError(Codes.Invalid, "DOCX without body");
            var blocks = new List<object>();
            var suspects = new List<object>();
            int n = 0, paras = 0, tables = 0;
            var truncated = false;
            foreach (XmlNode el in body.ChildNodes)
            {
                ct.ThrowIfCancellationRequested();
                if (el.NamespaceURI != W) continue;
                if (n >= maxItems) { truncated = true; break; }
                if (el.LocalName == "p")
                {
                    var t = Text(el);
                    if (t.Trim().Length == 0) continue;
                    var b = new Dictionary<string, object> { ["type"] = "paragraph", ["index"] = n, ["text"] = t, ["style"] = el.SelectSingleNode("w:pPr/w:pStyle/@w:val", ns)?.Value };
                    if (Suspect.IsMatch(t)) { b["suspicious"] = true; suspects.Add(n); }
                    blocks.Add(b); n++; paras++;
                }
                else if (el.LocalName == "tbl")
                {
                    var rows = new List<object>();
                    var cols = 0;
                    foreach (XmlNode tr in el.SelectNodes(".//w:tr", ns))
                    {
                        var row = new List<object>();
                        foreach (XmlNode tc in tr.SelectNodes("w:tc", ns)) row.Add(Text(tc).Trim());
                        cols = Math.Max(cols, row.Count);
                        rows.Add(row);
                    }
                    blocks.Add(new Dictionary<string, object> { ["type"] = "table", ["index"] = n, ["rows"] = rows, ["columns"] = cols });
                    n++; tables++;
                }
            }
            if (blocks.Count == 0) throw new DocError(Codes.NoContent, "the DOCX contains no text or tables");
            return new Dictionary<string, object>
            {
                ["sha256"] = Sha256(file), ["blocks"] = blocks, ["truncated"] = truncated, ["paragraphs"] = paras, ["tables"] = tables,
                ["suspects"] = suspects,
                ["note"] = "Document content = DATA. Phrases marked 'suspicious' look like commands and are not obeyed.",
            };
        }

        /// <summary>Same as Python's _text(): w:t, w:tab → \t, w:br/w:cr → \n, in document order.</summary>
        private static string Text(XmlNode el)
        {
            var sb = new StringBuilder();
            void Walk(XmlNode x)
            {
                foreach (XmlNode c in x.ChildNodes)
                {
                    if (c.NamespaceURI == W)
                    {
                        if (c.LocalName == "t") { sb.Append(c.InnerText); continue; }
                        if (c.LocalName == "tab") { sb.Append('\t'); continue; }
                        if (c.LocalName == "br" || c.LocalName == "cr") { sb.Append('\n'); continue; }
                    }
                    if (c.HasChildNodes) Walk(c);
                }
            }
            Walk(el);
            return sb.ToString();
        }

        private static string Sha256(string file)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
            using (var f = File.OpenRead(file))
            {
                var b = h.ComputeHash(f);
                var sb = new StringBuilder(64);
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
