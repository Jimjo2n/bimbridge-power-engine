// SPDX-License-Identifier: GPL-3.0-only
//! BIMBRIDGE Power Engine — Rust Geometry Engine.
//!
//! Geometry kernels over immutable snapshots (flat f64 buffers) sent by the C# core.
//! Same definitions and tie-breaks as `BimBridge.PowerEngine.GeometryKernels` (C#), so results can be compared exactly.
//! Does not access Revit, files or the network. Two transports: FFI (cdylib, `pe_*`) and IPC (`bimbridge-power-engine` binary).

pub mod kernels;
pub mod ffi;

pub const VERSION: &str = env!("CARGO_PKG_VERSION");
/// Version of the Power Engine contract this engine implements.
pub const PROTOCOL: u32 = 1;
