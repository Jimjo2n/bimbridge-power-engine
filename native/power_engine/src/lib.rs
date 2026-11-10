// SPDX-License-Identifier: GPL-3.0-only
//! BIMBRIDGE Power Engine — Rust Geometry Engine.
//!
//! Núcleos geométricos sobre instantáneas inmutables (búferes planos de f64) enviados por el núcleo C#.
//! Mismas definiciones y desempates que `BimBridge.PowerEngine.GeometryKernels` (C#), para comparar resultados exactos.
//! No accede a Revit, a archivos ni a la red. Dos transportes: FFI (cdylib, `pe_*`) e IPC (binario `bimbridge-power-engine`).

pub mod kernels;
pub mod ffi;

pub const VERSION: &str = env!("CARGO_PKG_VERSION");
/// Versión del contrato Power Engine que implementa este motor.
pub const PROTOCOL: u32 = 1;
