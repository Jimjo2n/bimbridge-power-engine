// SPDX-License-Identifier: GPL-3.0-only
//! pe=1 IPC worker protocol, running the real binary.
use serde_json::{json, Value};
use std::io::Write;
use std::process::{Command, Stdio};

fn exchange(lines: &[String]) -> Vec<Value> {
    let mut child = Command::new(env!("CARGO_BIN_EXE_bimbridge-power-engine"))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    {
        let mut stdin = child.stdin.take().unwrap();
        for l in lines {
            writeln!(stdin, "{l}").unwrap();
        }
    }
    let out = child.wait_with_output().unwrap();
    assert!(out.status.success());
    String::from_utf8(out.stdout).unwrap().lines().map(|l| serde_json::from_str(l).unwrap()).collect()
}

fn one(job: &str, args: Value) -> Value {
    let r = exchange(&[json!({"pe":1,"id":"t","job":job,"args":args}).to_string()]);
    assert_eq!(r.len(), 1);
    assert_eq!(r[0]["id"], "t");
    r[0].clone()
}

#[test]
fn hello_announces_all_jobs_and_rejects_other_versions() {
    let r = one("hello", json!({"pe_min":1,"pe_max":1}));
    assert_eq!(r["code"], "OK");
    assert_eq!(r["result"]["capabilities"].as_array().unwrap().len(), 6);
    assert_eq!(one("hello", json!({"pe_min":2,"pe_max":3}))["code"], "VERSION_INCOMPATIBLE");
    let bad = exchange(&[json!({"pe":2,"id":"v","job":"hello"}).to_string()]);
    assert_eq!(bad[0]["code"], "VERSION_INCOMPATIBLE");
}

#[test]
fn six_jobs_answer() {
    let t = one("transform3", json!({"xyz":[1.,2.,3.],"m":[1.,0.,0.,5., 0.,1.,0.,0., 0.,0.,1.,0.]}));
    assert_eq!(t["result"]["xyz"][0], 6.0);
    let n = one("nearest", json!({"dim":2,"query":[0.,0.,50.,50.],"reference":[1.,0.],"max_dist":5.}));
    assert_eq!(n["result"]["index"], json!([0, -1]));
    assert_eq!(n["result"]["distance"][1], Value::Null, "no neighbour: NaN travels as null");
    let m = one("match", json!({"dim":2,"expected_items":[0.,0.],"observed_items":[0.,0.1],"tol":1.}));
    assert_eq!(m["result"]["pair"], json!([0]));
    let d = one("duplicates", json!({"dim":3,"points":[0.,0.,0.,0.,0.,0.,9.,9.,9.],"tol":0.1}));
    let g = d["result"]["group"].as_array().unwrap();
    assert_eq!(g[0], g[1]);
    assert_ne!(g[0], g[2]);
    let s = one("segment_contacts", json!({"segments_list":[0.,0.,2.,2.,0.,2.,2.,0.],"eps":1e-9}));
    assert_eq!(s["code"], "OK");
    assert!(!s["result"]["contacts"].as_array().unwrap().is_empty());
    let c = one("components", json!({"nodes":3,"edges":[0,1]}));
    assert_eq!(c["result"]["degree"], json!([1, 1, 0]));
}

#[test]
fn invalid_inputs_fail_closed_and_worker_keeps_serving() {
    let cases = [
        ("nearest", json!({"dim":4,"query":[],"reference":[],"max_dist":1.})),
        ("nearest", json!({"dim":2,"query":[1.],"reference":[1.,2.],"max_dist":1.})),
        ("nearest", json!({"dim":2,"query":["x",1.],"reference":[],"max_dist":1.})),
        ("match", json!({"dim":2,"expected_items":[0.,0.]})),
        ("transform3", json!({"xyz":[1.,2.],"m":[1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.]})),
        ("transform3", json!({"xyz":[1.,2.,3.],"m":[1.]})),
        ("components", json!({"nodes":2,"edges":[0,9]})),
        ("components", json!({"nodes":2,"edges":[0]})),
        ("components", json!({"nodes":2,"edges":[-1,0]})),
        ("components", json!({"nodes":-1,"edges":[]})),
        ("segment_contacts", json!({"segments_list":[1.,2.,3.],"eps":0.1})),
    ];
    for (job, args) in cases {
        let r = one(job, args.clone());
        assert_eq!(r["code"], "INVALID", "{job} {args}: {r}");
    }
    let r = exchange(&[
        "{not json".into(),
        json!({"pe":1,"id":"a","job":"invented"}).to_string(),
        json!({"pe":1,"id":"b","job":"components","args":{"nodes":1,"edges":[]}}).to_string(),
    ]);
    assert_eq!(r[0]["code"], "INVALID");
    assert_eq!(r[1]["code"], "NOT_SUPPORTED");
    assert_eq!(r[2]["code"], "OK");
    assert_eq!(r[2]["id"], "b");
}

#[test]
fn exit_message_ends_without_response() {
    let r = exchange(&[json!({"pe":1,"id":"x","job":"exit"}).to_string(), json!({"pe":1,"id":"y","job":"hello"}).to_string()]);
    assert!(r.is_empty());
}

#[test]
fn eof_ends_cleanly() {
    assert!(exchange(&[]).is_empty());
}

#[test]
fn oversized_line_is_refused_and_does_not_hang() {
    let mut child = Command::new(env!("CARGO_BIN_EXE_bimbridge-power-engine"))
        .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null()).spawn().unwrap();
    let mut stdin = child.stdin.take().unwrap();
    let writer = std::thread::spawn(move || {
        // The worker cuts off above 64 MiB: a broken-pipe error on the writer is expected.
        let chunk = vec![b' '; 1024 * 1024];
        for _ in 0..70 {
            if stdin.write_all(&chunk).is_err() { return; }
        }
        let _ = stdin.write_all(b"\n");
    });
    let out = child.wait_with_output().unwrap();
    writer.join().unwrap();
    let r: Value = serde_json::from_str(String::from_utf8(out.stdout).unwrap().lines().next().unwrap()).unwrap();
    assert_eq!(r["code"], "TOO_LARGE");
}

#[test]
fn nan_result_travels_as_null_and_non_finite_parameters_are_refused() {
    let n = one("nearest", json!({"dim":2,"query":[0.,0.],"reference":[],"max_dist":1.}));
    assert_eq!(n["result"]["index"], json!([-1]));
    assert_eq!(n["result"]["distance"], json!([null]));
    let bad = exchange(&[r#"{"pe":1,"id":"z","job":"nearest","args":{"dim":2,"query":[0,0],"reference":[],"max_dist":null}}"#.into()]);
    assert_eq!(bad[0]["code"], "INVALID");
}
