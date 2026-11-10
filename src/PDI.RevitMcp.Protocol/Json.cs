// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PDI.RevitMcp.Protocol
{
    /// <summary>
    /// JSON mínimo y sin dependencias. Existe para que el add-in no cargue ninguna DLL
    /// externa dentro de Revit (choques de versiones en 2022–2024).
    /// Objetos → Dictionary&lt;string, object&gt;, arreglos → List&lt;object&gt;,
    /// números enteros → long, decimales → double, true/false → bool, null → null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipWs();
            var value = p.ReadValue(0);
            p.SkipWs();
            if (!p.AtEnd) throw p.Error("texto sobrante después del valor");
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            var o = Parse(text) as Dictionary<string, object>;
            if (o == null) throw new FormatException("JSON: se esperaba un objeto");
            return o;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        // ---------------- escritura ----------------

        private static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("JSON: anidamiento excesivo");
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case uint ui: sb.Append(ui.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case double d: WriteDouble(sb, d); return;
                case float f: WriteDouble(sb, f); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        WriteValue(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                case IJsonWritable w:
                    WriteValue(sb, w.ToJson(), depth + 1);
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        WriteValue(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
            }
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            // "R" conserva el valor exacto; quitamos ceros innecesarios para ahorrar tokens.
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- lectura ----------------

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; }
            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string msg) =>
                new FormatException("JSON: " + msg + " (posición " + _i + ")");

            public void SkipWs()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
            }

            public object ReadValue(int depth)
            {
                if (depth > 64) throw Error("anidamiento excesivo");
                if (AtEnd) throw Error("fin inesperado");
                var c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error("carácter inesperado '" + c + "'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error("se esperaba " + word);
                _i += word.Length;
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++; SkipWs();
                if (!AtEnd && _s[_i] == '}') { _i++; return d; }
                while (true)
                {
                    SkipWs();
                    if (AtEnd || _s[_i] != '"') throw Error("se esperaba una clave");
                    var key = ReadString();
                    SkipWs();
                    if (AtEnd || _s[_i] != ':') throw Error("se esperaba ':'");
                    _i++; SkipWs();
                    d[key] = ReadValue(depth + 1);
                    SkipWs();
                    if (AtEnd) throw Error("objeto sin cerrar");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return d; }
                    throw Error("se esperaba ',' o '}'");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var l = new List<object>();
                _i++; SkipWs();
                if (!AtEnd && _s[_i] == ']') { _i++; return l; }
                while (true)
                {
                    SkipWs();
                    l.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (AtEnd) throw Error("arreglo sin cerrar");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return l; }
                    throw Error("se esperaba ',' o ']'");
                }
            }

            private string ReadString()
            {
                _i++; // comilla inicial
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("cadena sin cerrar");
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw Error("escape incompleto");
                    var e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("escape \\u incompleto");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error("escape desconocido");
                    }
                }
            }

            private object ReadNumber()
            {
                var start = _i;
                if (_s[_i] == '-') _i++;
                while (!AtEnd && char.IsDigit(_s[_i])) _i++;
                var isFloat = false;
                if (!AtEnd && _s[_i] == '.') { isFloat = true; _i++; while (!AtEnd && char.IsDigit(_s[_i])) _i++; }
                if (!AtEnd && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    isFloat = true; _i++;
                    if (!AtEnd && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    while (!AtEnd && char.IsDigit(_s[_i])) _i++;
                }
                var token = _s.Substring(start, _i - start);
                if (!isFloat && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw Error("número inválido '" + token + "'");
            }
        }
    }

    /// <summary>Objetos que saben convertirse a la forma JSON (diccionarios y listas).</summary>
    public interface IJsonWritable
    {
        object ToJson();
    }
}
