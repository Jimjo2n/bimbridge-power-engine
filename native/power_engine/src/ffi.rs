// SPDX-License-Identifier: GPL-3.0-only
//! Interfaz C (FFI) del Rust Geometry Engine. Códigos de retorno: 0 OK, 1 INVALIDO, 2 CANCELADO, 3 ERROR_MOTOR (pánico capturado).
//! El llamador (C#) reserva los búferes de salida; la única memoria que devuelve Rust (pares de segmentos) se libera con
//! `pe_free_i32`. Ningún pánico cruza la frontera FFI.

use crate::kernels::{self, Cancel, PeError};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::slice;
use std::sync::atomic::AtomicBool;

pub const PE_OK: i32 = 0;
pub const PE_INVALID: i32 = 1;
pub const PE_CANCELLED: i32 = 2;
pub const PE_PANIC: i32 = 3;

fn code(r: Result<(), PeError>) -> i32 {
    match r {
        Ok(()) => PE_OK,
        Err(PeError::Invalid(_)) => PE_INVALID,
        Err(PeError::Cancelled) => PE_CANCELLED,
    }
}

fn guard<F: FnOnce() -> i32>(f: F) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(PE_PANIC)
}

unsafe fn cancel<'a>(flag: *const u8) -> Cancel<'a> {
    if flag.is_null() {
        Cancel(None)
    } else {
        // u8 de C#: 0 = seguir, ≠0 = cancelar. AtomicBool tiene el mismo tamaño y alineación que u8.
        Cancel(Some(&*(flag as *const AtomicBool)))
    }
}

unsafe fn sl<'a, T>(p: *const T, n: usize) -> &'a [T] {
    if n == 0 || p.is_null() { &[] } else { slice::from_raw_parts(p, n) }
}

unsafe fn slm<'a, T>(p: *mut T, n: usize) -> &'a mut [T] {
    if n == 0 || p.is_null() { &mut [] } else { slice::from_raw_parts_mut(p, n) }
}

#[no_mangle]
pub extern "C" fn pe_protocol() -> u32 {
    crate::PROTOCOL
}

/// Versión como entero mayor*10000 + menor*100 + parche.
#[no_mangle]
pub extern "C" fn pe_version() -> u32 {
    let v: Vec<u32> = crate::VERSION.split('.').map(|x| x.parse().unwrap_or(0)).collect();
    v.first().copied().unwrap_or(0) * 10000 + v.get(1).copied().unwrap_or(0) * 100 + v.get(2).copied().unwrap_or(0)
}

/// # Safety
/// Punteros válidos para n*3 (xyz, out) y 12 (m) f64.
#[no_mangle]
pub unsafe extern "C" fn pe_transform3(xyz: *const f64, n: usize, m: *const f64, out: *mut f64, cancel_flag: *const u8) -> i32 {
    guard(|| code(kernels::transform3(sl(xyz, n * 3), sl(m, 12), slm(out, n * 3), &cancel(cancel_flag))))
}

/// # Safety
/// Búferes de tamaño nq*dim, nr*dim, nq, nq.
#[no_mangle]
pub unsafe extern "C" fn pe_nearest(q: *const f64, nq: usize, r: *const f64, nr: usize, dim: usize, max_dist: f64,
                                    out_idx: *mut i32, out_dist: *mut f64, cancel_flag: *const u8) -> i32 {
    guard(|| code(kernels::nearest(sl(q, nq * dim), sl(r, nr * dim), dim, max_dist, slm(out_idx, nq), slm(out_dist, nq), &cancel(cancel_flag))))
}

/// # Safety
/// Búferes de tamaño ne*dim, no*dim, ne.
#[no_mangle]
pub unsafe extern "C" fn pe_match(e: *const f64, ne: usize, o: *const f64, no: usize, dim: usize, tol: f64, out: *mut i32, cancel_flag: *const u8) -> i32 {
    guard(|| code(kernels::match_points(sl(e, ne * dim), sl(o, no * dim), dim, tol, slm(out, ne), &cancel(cancel_flag))))
}

/// # Safety
/// Búferes de tamaño n*dim y n.
#[no_mangle]
pub unsafe extern "C" fn pe_duplicates(p: *const f64, n: usize, dim: usize, tol: f64, out: *mut i32, cancel_flag: *const u8) -> i32 {
    guard(|| code(kernels::duplicates(sl(p, n * dim), dim, tol, slm(out, n), &cancel(cancel_flag))))
}

/// Pares en contacto: devuelve en *out un búfer de 3*count i32 (i, j, tipo) que el llamador libera con pe_free_i32(ptr, 3*count).
/// # Safety
/// s: n*4 f64; out y count no nulos.
#[no_mangle]
pub unsafe extern "C" fn pe_segment_contacts(s: *const f64, n: usize, eps: f64, out: *mut *mut i32, count: *mut usize, cancel_flag: *const u8) -> i32 {
    guard(|| {
        if out.is_null() || count.is_null() {
            return PE_INVALID;
        }
        *out = std::ptr::null_mut();
        *count = 0;
        match kernels::segment_contacts(sl(s, n * 4), eps, &cancel(cancel_flag)) {
            Ok(v) => {
                let mut flat: Vec<i32> = Vec::with_capacity(v.len() * 3);
                for (i, j, k) in &v {
                    flat.extend_from_slice(&[*i, *j, *k]);
                }
                let mut b = flat.into_boxed_slice();
                *count = v.len();
                *out = b.as_mut_ptr();
                std::mem::forget(b);
                PE_OK
            }
            Err(e) => code(Err(e)),
        }
    })
}

/// # Safety
/// ptr devuelto por pe_segment_contacts con su longitud exacta.
#[no_mangle]
pub unsafe extern "C" fn pe_free_i32(ptr: *mut i32, len: usize) {
    if !ptr.is_null() && len > 0 {
        drop(Box::from_raw(slice::from_raw_parts_mut(ptr, len)));
    }
}

/// # Safety
/// edges: ne*2 i32; comp y deg: nodes i32.
#[no_mangle]
pub unsafe extern "C" fn pe_components(nodes: usize, edges: *const i32, ne: usize, comp: *mut i32, deg: *mut i32) -> i32 {
    guard(|| code(kernels::components(nodes, sl(edges, ne * 2), slm(comp, nodes), slm(deg, nodes))))
}
