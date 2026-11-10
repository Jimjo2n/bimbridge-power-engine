// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// Power Engine — acceso FFI al Rust Geometry Engine (bimbridge_power_engine.dll / libbimbridge_power_engine.so).
    /// Mismo contrato de datos que <see cref="GeometryKernels"/>: búferes planos que reserva C#; Rust no conserva punteros.
    /// Si la biblioteca no está, <see cref="Available"/> es false y el dispatcher usa C# (degradación controlada).
    /// Corre DENTRO del proceso que lo llama (AgentHost), nunca dentro de Revit.
    /// </summary>
    public static class RustGeometryEngine
    {
        private const string Lib = "bimbridge_power_engine";
        public const int Ok = 0, Invalid = 1, Cancelled = 2, Panic = 3;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern uint pe_protocol();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern uint pe_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int pe_transform3(double[] xyz, UIntPtr n, double[] m, [Out] double[] o, IntPtr cancel);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int pe_nearest(double[] q, UIntPtr nq, double[] r, UIntPtr nr, UIntPtr dim, double maxDist, [Out] int[] idx, [Out] double[] dist, IntPtr cancel);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int pe_match(double[] e, UIntPtr ne, double[] o, UIntPtr no, UIntPtr dim, double tol, [Out] int[] res, IntPtr cancel);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int pe_duplicates(double[] p, UIntPtr n, UIntPtr dim, double tol, [Out] int[] res, IntPtr cancel);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int pe_segment_contacts(double[] s, UIntPtr n, double eps, out IntPtr res, out UIntPtr count, IntPtr cancel);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void pe_free_i32(IntPtr p, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int pe_components(UIntPtr nodes, int[] edges, UIntPtr ne, [Out] int[] comp, [Out] int[] deg);

        private static readonly Lazy<string> _probe = new Lazy<string>(() =>
        {
            try
            {
                var p = pe_protocol();
                return p >= PowerEngineContracts.MinSupported && p <= PowerEngineContracts.ProtocolVersion ? null : "contrato " + p + " incompatible";
            }
            catch (DllNotFoundException ex) { return "biblioteca no encontrada: " + ex.Message; }
            catch (EntryPointNotFoundException ex) { return "biblioteca incompatible: " + ex.Message; }
            catch (BadImageFormatException ex) { return "arquitectura incompatible: " + ex.Message; }
        });

        public static bool Available => _probe.Value == null;
        public static string Unavailable => _probe.Value;
        public static string Version { get { var v = pe_version(); return v / 10000 + "." + v / 100 % 100 + "." + v % 100; } }

        /// <summary>Bandera de cancelación fijada en memoria para que Rust la lea mientras C# puede activarla desde otro hilo.</summary>
        private sealed class CancelFlag : IDisposable
        {
            private readonly byte[] _b = new byte[1];
            private GCHandle _h;
            private readonly CancellationTokenRegistration _reg;
            public CancelFlag(CancellationToken ct)
            {
                _h = GCHandle.Alloc(_b, GCHandleType.Pinned);
                if (ct.CanBeCanceled) _reg = ct.Register(() => Volatile.Write(ref _b[0], (byte)1));
                if (ct.IsCancellationRequested) _b[0] = 1;
            }
            public IntPtr Ptr => _h.AddrOfPinnedObject();
            public void Dispose() { _reg.Dispose(); if (_h.IsAllocated) _h.Free(); }
        }

        private static void Check(int rc, string what, CancellationToken ct)
        {
            if (rc == Ok) return;
            if (rc == Cancelled) throw new OperationCanceledException("Rust " + what + ": cancelado", ct);
            if (rc == Invalid) throw new ArgumentException("Rust " + what + ": datos inválidos (mismas reglas que GeometryKernels)");
            throw new InvalidOperationException("Rust " + what + ": error interno del motor (código " + rc + ")");
        }

        private static UIntPtr U(int n) => new UIntPtr((uint)n);

        public static double[] Transform3(double[] xyz, double[] m, CancellationToken ct = default(CancellationToken))
        {
            if (m == null || m.Length != 12) throw new ArgumentException("matriz 3×4 (12 valores)");
            var o = new double[xyz.Length];
            using (var c = new CancelFlag(ct)) Check(pe_transform3(xyz, U(xyz.Length / 3), m, o, c.Ptr), "transform3", ct);
            return o;
        }

        public static (int[] Index, double[] Dist) Nearest(double[] q, double[] r, int dim, double maxDist, CancellationToken ct = default(CancellationToken))
        {
            var n = q.Length / dim;
            var idx = new int[n]; var dist = new double[n];
            using (var c = new CancelFlag(ct)) Check(pe_nearest(q, U(n), r, U(r.Length / dim), U(dim), maxDist, idx, dist, c.Ptr), "nearest", ct);
            return (idx, dist);
        }

        public static int[] Match(double[] e, double[] o, int dim, double tol, CancellationToken ct = default(CancellationToken))
        {
            var res = new int[e.Length / dim];
            using (var c = new CancelFlag(ct)) Check(pe_match(e, U(e.Length / dim), o, U(o.Length / dim), U(dim), tol, res, c.Ptr), "match", ct);
            return res;
        }

        public static int[] Duplicates(double[] p, int dim, double tol, CancellationToken ct = default(CancellationToken))
        {
            var res = new int[p.Length / dim];
            using (var c = new CancelFlag(ct)) Check(pe_duplicates(p, U(p.Length / dim), U(dim), tol, res, c.Ptr), "duplicates", ct);
            return res;
        }

        public static List<(int I, int J, int Kind)> SegmentContacts(double[] s, double eps = 1e-7, CancellationToken ct = default(CancellationToken))
        {
            IntPtr ptr; UIntPtr count;
            using (var c = new CancelFlag(ct)) Check(pe_segment_contacts(s, U(s.Length / 4), eps, out ptr, out count, c.Ptr), "segment_contacts", ct);
            var n = (int)count.ToUInt32();
            var flat = new int[n * 3];
            try { if (n > 0) Marshal.Copy(ptr, flat, 0, flat.Length); }
            finally { pe_free_i32(ptr, U(n * 3)); }
            var res = new List<(int, int, int)>(n);
            for (var k = 0; k < n; k++) res.Add((flat[3 * k], flat[3 * k + 1], flat[3 * k + 2]));
            return res;
        }

        public static (int[] Component, int[] Degree) Components(int nodes, int[] edges)
        {
            var comp = new int[nodes]; var deg = new int[nodes];
            Check(pe_components(U(nodes), edges, U(edges.Length / 2), comp, deg), "components", CancellationToken.None);
            return (comp, deg);
        }
    }
}
