// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// 0.9.1 (indexador completo): todos los símbolos públicos de un ensamblado de referencia, con firma completa, leídos con
/// System.Reflection.Metadata (sin cargar ni ejecutar Revit). Una línea por símbolo, ordenadas (determinista):
///   T|Namespace.Tipo|class|struct|interface|enum|delegate|base=…|impl=…|obsolete=…
///   C|Tipo::.ctor(Param,…)                       constructor
///   M|Tipo::Nombre(Param,…)|ret=…|static|obsolete=…
///   P|Tipo::Nombre|type=…|get|set|static
///   F|Tipo::Nombre|type=…|static|const          (miembros de enum: F|Enum::Miembro|enum)
///   E|Tipo::Nombre|type=…
/// Sólo es PRESENCIA técnica: no se infieren precondiciones, transacciones ni comportamiento a partir del nombre.
/// </summary>
internal static class Symbols
{
    private sealed class Provider : ISignatureTypeProvider<string, object>
    {
        private readonly MetadataReader _md;
        public Provider(MetadataReader md) { _md = md; }
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Name(r, r.GetTypeDefinition(h));
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
        {
            var t = r.GetTypeReference(h);
            var ns = r.GetString(t.Namespace);
            return (ns.Length > 0 ? ns + "." : "") + r.GetString(t.Name);
        }
        public string GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, g);
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetPointerType(string e) => e + "*";
        public string GetPinnedType(string e) => e;
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
        public string GetGenericMethodParameter(object g, int i) => "!!" + i;
        public string GetGenericTypeParameter(object g, int i) => "!" + i;
        public string GetModifiedType(string m, string u, bool req) => u;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    }

    private static string Name(MetadataReader md, TypeDefinition t)
    {
        var n = md.GetString(t.Name);
        if (t.IsNested) return Name(md, md.GetTypeDefinition(t.GetDeclaringType())) + "+" + n;
        var ns = md.GetString(t.Namespace);
        return (ns.Length > 0 ? ns + "." : "") + n;
    }

    private static bool PublicType(MetadataReader md, TypeDefinition t)
    {
        var v = t.Attributes & TypeAttributes.VisibilityMask;
        if (v == TypeAttributes.Public) return true;
        if (v == TypeAttributes.NestedPublic) return PublicType(md, md.GetTypeDefinition(t.GetDeclaringType()));
        return false;
    }

    private static string Obsolete(MetadataReader md, CustomAttributeHandleCollection attrs)
    {
        foreach (var h in attrs)
        {
            var a = md.GetCustomAttribute(h);
            string typeName = null;
            if (a.Constructor.Kind == HandleKind.MemberReference)
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
                if (mr.Parent.Kind == HandleKind.TypeReference) typeName = md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
            }
            else if (a.Constructor.Kind == HandleKind.MethodDefinition)
                typeName = md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType()).Name);
            if (typeName != "ObsoleteAttribute") continue;
            // El primer argumento (mensaje) se lee del blob sin decodificar tipos: prolog 0x0001 + SerString.
            var blob = md.GetBlobReader(a.Value);
            if (blob.Length > 2 && blob.ReadUInt16() == 1)
            {
                try { var msg = blob.ReadSerializedString(); return "obsolete=" + (msg ?? "").Replace("|", "/").Replace("\n", " ").Replace("\r", " "); }
                catch (BadImageFormatException) { }
            }
            return "obsolete";
        }
        return null;
    }

    public static List<string> Extract(string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        var p = new Provider(md);
        var lines = new List<string>();
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            if (!PublicType(md, t)) continue;
            var tn = Name(md, t);
            string baseName = null;
            if (!t.BaseType.IsNil)
                baseName = t.BaseType.Kind == HandleKind.TypeDefinition ? Name(md, md.GetTypeDefinition((TypeDefinitionHandle)t.BaseType))
                         : t.BaseType.Kind == HandleKind.TypeReference ? p.GetTypeFromReference(md, (TypeReferenceHandle)t.BaseType, 0) : "spec";
            var kind = (t.Attributes & TypeAttributes.Interface) != 0 ? "interface"
                     : baseName == "System.Enum" ? "enum" : baseName == "System.ValueType" ? "struct"
                     : baseName == "System.MulticastDelegate" ? "delegate" : "class";
            var impl = t.GetInterfaceImplementations().Select(ih =>
            {
                var i = md.GetInterfaceImplementation(ih).Interface;
                return i.Kind == HandleKind.TypeDefinition ? Name(md, md.GetTypeDefinition((TypeDefinitionHandle)i))
                     : i.Kind == HandleKind.TypeReference ? p.GetTypeFromReference(md, (TypeReferenceHandle)i, 0) : "spec";
            }).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var tl = "T|" + tn + "|" + kind;
            if (baseName != null && kind == "class" && baseName != "System.Object") tl += "|base=" + baseName;
            if (impl.Count > 0) tl += "|impl=" + string.Join(",", impl);
            var to = Obsolete(md, t.GetCustomAttributes()); if (to != null) tl += "|" + to;
            lines.Add(tl);

            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if ((m.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
                var name = md.GetString(m.Name);
                if ((m.Attributes & MethodAttributes.SpecialName) != 0 && name != ".ctor" && name != ".cctor") continue;   // accesores: van en P/E
                if (name == ".cctor") continue;
                var sig = m.DecodeSignature(p, null);
                var args = string.Join(",", sig.ParameterTypes);
                var st = (m.Attributes & MethodAttributes.Static) != 0;
                var ob = Obsolete(md, m.GetCustomAttributes());
                lines.Add(name == ".ctor"
                    ? "C|" + tn + "::.ctor(" + args + ")" + (ob != null ? "|" + ob : "")
                    : "M|" + tn + "::" + name + "(" + args + ")|ret=" + sig.ReturnType + (st ? "|static" : "") + (ob != null ? "|" + ob : ""));
            }
            foreach (var ph in t.GetProperties())
            {
                var pr = md.GetPropertyDefinition(ph);
                var acc = pr.GetAccessors();
                bool Pub(MethodDefinitionHandle h) => !h.IsNil && (md.GetMethodDefinition(h).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                var get = Pub(acc.Getter); var set = Pub(acc.Setter);
                if (!get && !set) continue;
                var sig = pr.DecodeSignature(p, null);
                var st = !acc.Getter.IsNil && (md.GetMethodDefinition(acc.Getter).Attributes & MethodAttributes.Static) != 0;
                var idx = sig.ParameterTypes.Length > 0 ? "[" + string.Join(",", sig.ParameterTypes) + "]" : "";
                var ob = Obsolete(md, pr.GetCustomAttributes());
                lines.Add("P|" + tn + "::" + md.GetString(pr.Name) + idx + "|type=" + sig.ReturnType + (get ? "|get" : "") + (set ? "|set" : "") + (st ? "|static" : "") + (ob != null ? "|" + ob : ""));
            }
            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if ((f.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public) continue;
                var name = md.GetString(f.Name);
                if (kind == "enum") { lines.Add("F|" + tn + "::" + name + "|enum"); continue; }
                var ob = Obsolete(md, f.GetCustomAttributes());
                lines.Add("F|" + tn + "::" + name + "|type=" + f.DecodeSignature(p, null) + ((f.Attributes & FieldAttributes.Static) != 0 ? "|static" : "") +
                          ((f.Attributes & FieldAttributes.Literal) != 0 ? "|const" : "") + (ob != null ? "|" + ob : ""));
            }
            foreach (var eh in t.GetEvents())
            {
                var e = md.GetEventDefinition(eh);
                var adder = e.GetAccessors().Adder;
                if (adder.IsNil || (md.GetMethodDefinition(adder).Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
                var et = e.Type.Kind == HandleKind.TypeReference ? p.GetTypeFromReference(md, (TypeReferenceHandle)e.Type, 0)
                       : e.Type.Kind == HandleKind.TypeDefinition ? Name(md, md.GetTypeDefinition((TypeDefinitionHandle)e.Type)) : "spec";
                lines.Add("E|" + tn + "::" + md.GetString(e.Name) + "|type=" + et);
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    /// <summary>Clave estable del símbolo (sin atributos variables): sirve para comparar versiones.</summary>
    public static string Key(string line)
    {
        var parts = line.Split('|');
        return parts[0] + "|" + parts[1];
    }

    public static string Sha256(byte[] data) { using var s = SHA256.Create(); return Convert.ToHexString(s.ComputeHash(data)).ToLowerInvariant(); }

    /// <summary>Escribe el listado comprimido (gzip determinista: .NET no guarda fecha) y devuelve el SHA-256 del texto sin comprimir.</summary>
    public static string WriteGz(string path, List<string> lines)
    {
        var text = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
            gz.Write(text, 0, text.Length);
        return Sha256(text);
    }

    public static List<string> ReadGz(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
