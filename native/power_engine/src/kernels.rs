// SPDX-License-Identifier: GPL-3.0-only
//! Núcleos geométricos. Determinismo: mismos resultados que la referencia C# (desempates por índice).

use std::sync::atomic::{AtomicBool, Ordering};

#[derive(Debug, PartialEq)]
pub enum PeError {
    Invalid(String),
    Cancelled,
}

pub type PeResult<T> = Result<T, PeError>;

/// La cancelación se consulta por TRABAJO hecho (puntos visitados), no por índice: un grupo patológico en una sola
/// celda (n² comparaciones) también se cancela en milisegundos. Mismo criterio en C# (GeometryKernels.WorkCheck).
pub const WORK_CHECK: usize = 1 << 16;

/// Bandera de cancelación compartida (el núcleo C# la activa; los bucles la consultan cada bloque).
pub struct Cancel<'a>(pub Option<&'a AtomicBool>);

impl<'a> Cancel<'a> {
    #[inline]
    pub fn check(&self) -> PeResult<()> {
        match self.0 {
            Some(f) if f.load(Ordering::Relaxed) => Err(PeError::Cancelled),
            _ => Ok(()),
        }
    }
}

fn check_finite(v: &[f64], what: &str) -> PeResult<()> {
    for (i, x) in v.iter().enumerate() {
        if !x.is_finite() {
            return Err(PeError::Invalid(format!("{what}: valor no finito en la posición {i}")));
        }
    }
    Ok(())
}

/// Umbral a partir del cual se reparte el trabajo entre hilos.
pub const PAR_MIN: usize = 50_000;

fn threads() -> usize {
    std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1).min(16)
}

// ---------------- transformaciones ----------------

pub fn transform3(xyz: &[f64], m: &[f64], out: &mut [f64], c: &Cancel) -> PeResult<()> {
    if m.len() != 12 {
        return Err(PeError::Invalid("matriz 3x4 (12 valores)".into()));
    }
    check_finite(m, "matriz")?;
    if xyz.len() % 3 != 0 || out.len() != xyz.len() {
        return Err(PeError::Invalid("búferes xyz inválidos".into()));
    }
    let n = xyz.len() / 3;
    let work = |src: &[f64], dst: &mut [f64]| {
        for (p, o) in src.chunks_exact(3).zip(dst.chunks_exact_mut(3)) {
            let (x, y, z) = (p[0], p[1], p[2]);
            o[0] = m[0] * x + m[1] * y + m[2] * z + m[3];
            o[1] = m[4] * x + m[5] * y + m[6] * z + m[7];
            o[2] = m[8] * x + m[9] * y + m[10] * z + m[11];
        }
    };
    if n < PAR_MIN {
        c.check()?;
        work(xyz, out);
        return Ok(());
    }
    let t = threads();
    let per = n.div_ceil(t) * 3;
    std::thread::scope(|s| {
        for (src, dst) in xyz.chunks(per).zip(out.chunks_mut(per)) {
            s.spawn(move || work(src, dst));
        }
    });
    c.check()
}

// ---------------- rejilla ----------------

pub struct Grid<'a> {
    dim: usize,
    cell: f64,
    p: &'a [f64],
    keys: Vec<i64>,
    idx: Vec<u32>,
    o: [f64; 3],
}

#[inline]
fn pack(cx: i64, cy: i64, cz: i64) -> i64 {
    ((cx + (1 << 20)) << 42) | ((cy + (1 << 20)) << 21) | (cz + (1 << 20))
}

impl<'a> Grid<'a> {
    pub fn new(p: &'a [f64], dim: usize, cell: f64) -> PeResult<Self> {
        if dim != 2 && dim != 3 {
            return Err(PeError::Invalid("dim 2 o 3".into()));
        }
        if !(cell > 0.0) || !cell.is_finite() {
            return Err(PeError::Invalid("tamaño de celda > 0".into()));
        }
        let n = p.len() / dim;
        let mut o = [f64::MAX; 3];
        for i in 0..n {
            for d in 0..dim {
                o[d] = o[d].min(p[dim * i + d]);
            }
        }
        if n == 0 {
            o = [0.0; 3];
        }
        if dim == 2 {
            o[2] = 0.0;
        }
        let mut g = Grid { dim, cell, p, keys: Vec::with_capacity(n), idx: (0..n as u32).collect(), o };
        let mut pairs: Vec<(i64, u32)> = (0..n)
            .map(|i| {
                let z = if dim == 3 { p[dim * i + 2] } else { 0.0 };
                (g.key(p[dim * i], p[dim * i + 1], z), i as u32)
            })
            .collect();
        pairs.sort_unstable();
        g.keys = pairs.iter().map(|x| x.0).collect();
        g.idx = pairs.iter().map(|x| x.1).collect();
        Ok(g)
    }

    #[inline]
    fn cell_of(&self, v: f64, o: f64) -> i64 {
        ((v - o) / self.cell).floor() as i64
    }

    #[inline]
    fn key(&self, x: f64, y: f64, z: f64) -> i64 {
        let cz = if self.dim == 3 { self.cell_of(z, self.o[2]) } else { 0 };
        pack(self.cell_of(x, self.o[0]), self.cell_of(y, self.o[1]), cz)
    }

    #[inline]
    /// Visita los puntos de las celdas vecinas; devuelve cuántos visitó (trabajo, para consultar la cancelación por trabajo hecho).
    pub fn near<F: FnMut(usize)>(&self, x: f64, y: f64, z: f64, mut visit: F) -> usize {
        let mut visited = 0usize;
        let cx = self.cell_of(x, self.o[0]);
        let cy = self.cell_of(y, self.o[1]);
        let cz = if self.dim == 3 { self.cell_of(z, self.o[2]) } else { 0 };
        let zr: i64 = if self.dim == 3 { 1 } else { 0 };
        for dx in -1..=1 {
            for dy in -1..=1 {
                for dz in -zr..=zr {
                    let k = pack(cx + dx, cy + dy, cz + dz);
                    let lo = self.keys.partition_point(|&v| v < k);
                    let mut t = lo;
                    while t < self.keys.len() && self.keys[t] == k {
                        visit(self.idx[t] as usize);
                        visited += 1;
                        t += 1;
                    }
                }
            }
        }
        visited
    }

    #[inline]
    pub fn dist2(&self, i: usize, x: f64, y: f64, z: f64) -> f64 {
        let d = self.dim;
        let dx = self.p[d * i] - x;
        let dy = self.p[d * i + 1] - y;
        let dz = if d == 3 { self.p[d * i + 2] - z } else { 0.0 };
        dx * dx + dy * dy + dz * dz
    }
}

// ---------------- vecino más cercano ----------------

pub fn nearest(q: &[f64], r: &[f64], dim: usize, max_dist: f64, out_idx: &mut [i32], out_dist: &mut [f64], c: &Cancel) -> PeResult<()> {
    check_finite(q, "consulta")?;
    check_finite(r, "referencia")?;
    let g = Grid::new(r, dim, max_dist)?;
    let n = q.len() / dim;
    if out_idx.len() != n || out_dist.len() != n {
        return Err(PeError::Invalid("búferes de salida".into()));
    }
    let max2 = max_dist * max_dist;
    let one = |i: usize| -> (i32, f64, usize) {
        let x = q[dim * i];
        let y = q[dim * i + 1];
        let z = if dim == 3 { q[dim * i + 2] } else { 0.0 };
        let mut best: i64 = -1;
        let mut bd = f64::MAX;
        let w = g.near(x, y, z, |j| {
            let d = g.dist2(j, x, y, z);
            if d <= max2 && (d < bd || (d == bd && (j as i64) < best)) {
                bd = d;
                best = j as i64;
            }
        });
        if best < 0 { (-1, f64::NAN, w) } else { (best as i32, bd.sqrt(), w) }
    };
    if n < PAR_MIN {
        let mut work = WORK_CHECK;
        for i in 0..n {
            if work >= WORK_CHECK {
                c.check()?;
                work = 0;
            }
            let (a, b, w) = one(i);
            out_idx[i] = a;
            out_dist[i] = b;
            work += w + 1;
        }
        return Ok(());
    }
    let t = threads();
    let per = n.div_ceil(t);
    std::thread::scope(|s| {
        for (k, (oi, od)) in out_idx.chunks_mut(per).zip(out_dist.chunks_mut(per)).enumerate() {
            let one = &one;
            s.spawn(move || {
                let mut work = 0usize;
                for (off, (a, b)) in oi.iter_mut().zip(od.iter_mut()).enumerate() {
                    if work >= WORK_CHECK {
                        if c.check().is_err() {
                            return;
                        }
                        work = 0;
                    }
                    let r = one(k * per + off);
                    *a = r.0;
                    *b = r.1;
                    work += r.2 + 1;
                }
            });
        }
    });
    c.check()
}

// ---------------- emparejamiento ----------------

pub fn match_points(e: &[f64], o: &[f64], dim: usize, tol: f64, out: &mut [i32], c: &Cancel) -> PeResult<()> {
    check_finite(e, "esperados")?;
    check_finite(o, "observados")?;
    let g = Grid::new(o, dim, tol)?;
    let ne = e.len() / dim;
    let no = o.len() / dim;
    if out.len() != ne {
        return Err(PeError::Invalid("búfer de salida".into()));
    }
    let t2 = tol * tol;
    let mut cand: Vec<(f64, u32, u32)> = Vec::new();
    let mut work = WORK_CHECK;
    for i in 0..ne {
        if work >= WORK_CHECK {
            c.check()?;
            work = 0;
        }
        let x = e[dim * i];
        let y = e[dim * i + 1];
        let z = if dim == 3 { e[dim * i + 2] } else { 0.0 };
        work += 1 + g.near(x, y, z, |j| {
            let d = g.dist2(j, x, y, z);
            if d <= t2 {
                cand.push((d, i as u32, j as u32));
            }
        });
    }
    cand.sort_unstable_by(|a, b| a.0.partial_cmp(&b.0).unwrap().then(a.1.cmp(&b.1)).then(a.2.cmp(&b.2)));
    out.iter_mut().for_each(|v| *v = -1);
    let mut used = vec![false; no];
    for (_, i, j) in cand {
        let (i, j) = (i as usize, j as usize);
        if out[i] < 0 && !used[j] {
            out[i] = j as i32;
            used[j] = true;
        }
    }
    Ok(())
}

// ---------------- duplicados y union-find ----------------

pub struct UnionFind(Vec<u32>);

impl UnionFind {
    pub fn new(n: usize) -> Self {
        UnionFind((0..n as u32).collect())
    }
    pub fn find(&mut self, mut x: usize) -> usize {
        while self.0[x] as usize != x {
            let p = self.0[x] as usize;
            self.0[x] = self.0[p];
            x = self.0[x] as usize;
        }
        x
    }
    /// La raíz es siempre el menor índice (igual que C#).
    pub fn union(&mut self, a: usize, b: usize) {
        let a = self.find(a);
        let b = self.find(b);
        if a == b {
            return;
        }
        if a < b {
            self.0[b] = a as u32;
        } else {
            self.0[a] = b as u32;
        }
    }
}

pub fn duplicates(p: &[f64], dim: usize, tol: f64, out: &mut [i32], c: &Cancel) -> PeResult<()> {
    check_finite(p, "puntos")?;
    let n = p.len() / dim;
    if out.len() != n {
        return Err(PeError::Invalid("búfer de salida".into()));
    }
    let g = Grid::new(p, dim, tol)?;
    let mut uf = UnionFind::new(n);
    let t2 = tol * tol;
    let mut work = WORK_CHECK;
    for i in 0..n {
        if work >= WORK_CHECK {
            c.check()?;
            work = 0;
        }
        let x = p[dim * i];
        let y = p[dim * i + 1];
        let z = if dim == 3 { p[dim * i + 2] } else { 0.0 };
        let mut hits: Vec<usize> = Vec::new();
        work += 1 + g.near(x, y, z, |j| {
            if j > i && g.dist2(j, x, y, z) <= t2 {
                hits.push(j);
            }
        });
        for j in hits {
            uf.union(i, j);
        }
    }
    for i in 0..n {
        out[i] = uf.find(i) as i32;
    }
    Ok(())
}

// ---------------- segmentos ----------------

pub const CROSS: i32 = 1;
pub const TOUCH: i32 = 2;
pub const OVERLAP: i32 = 3;

#[inline]
fn sq(v: f64) -> f64 {
    v * v
}

pub fn classify(s: &[f64], i: usize, j: usize, eps: f64) -> i32 {
    let (p1x, p1y, p2x, p2y) = (s[4 * i], s[4 * i + 1], s[4 * i + 2], s[4 * i + 3]);
    let (q1x, q1y, q2x, q2y) = (s[4 * j], s[4 * j + 1], s[4 * j + 2], s[4 * j + 3]);
    let lp = (sq(p2x - p1x) + sq(p2y - p1y)).sqrt();
    let lq = (sq(q2x - q1x) + sq(q2y - q1y)).sqrt();
    if lp < eps || lq < eps {
        return 0;
    }
    let scale = 1.0f64.max(p1x.abs().max(p1y.abs()).max(q1x.abs().max(q1y.abs())));
    let tol = eps * scale;
    let cr = |ox: f64, oy: f64, ax: f64, ay: f64, bx: f64, by: f64| (ax - ox) * (by - oy) - (ay - oy) * (bx - ox);
    let d1 = cr(q1x, q1y, q2x, q2y, p1x, p1y) / lq;
    let d2 = cr(q1x, q1y, q2x, q2y, p2x, p2y) / lq;
    let d3 = cr(p1x, p1y, p2x, p2y, q1x, q1y) / lp;
    let d4 = cr(p1x, p1y, p2x, p2y, q2x, q2y) / lp;
    let (z1, z2, z3, z4) = (d1.abs() <= tol, d2.abs() <= tol, d3.abs() <= tol, d4.abs() <= tol);
    if z1 && z2 {
        let ux = (p2x - p1x) / lp;
        let uy = (p2y - p1y) / lp;
        let mut b0 = (q1x - p1x) * ux + (q1y - p1y) * uy;
        let mut b1 = (q2x - p1x) * ux + (q2y - p1y) * uy;
        if b0 > b1 {
            std::mem::swap(&mut b0, &mut b1);
        }
        let ov = lp.min(b1) - 0f64.max(b0);
        return if ov > tol { OVERLAP } else if ov >= -tol { TOUCH } else { 0 };
    }
    if ((d1 > tol && d2 < -tol) || (d1 < -tol && d2 > tol)) && ((d3 > tol && d4 < -tol) || (d3 < -tol && d4 > tol)) {
        return CROSS;
    }
    let on = |ax: f64, ay: f64, bx: f64, by: f64, px: f64, py: f64| {
        px >= ax.min(bx) - tol && px <= ax.max(bx) + tol && py >= ay.min(by) - tol && py <= ay.max(by) + tol
    };
    if (z1 && on(q1x, q1y, q2x, q2y, p1x, p1y))
        || (z2 && on(q1x, q1y, q2x, q2y, p2x, p2y))
        || (z3 && on(p1x, p1y, p2x, p2y, q1x, q1y))
        || (z4 && on(p1x, p1y, p2x, p2y, q2x, q2y))
    {
        return TOUCH;
    }
    0
}

pub fn segment_contacts(s: &[f64], eps: f64, c: &Cancel) -> PeResult<Vec<(i32, i32, i32)>> {
    check_finite(s, "segmentos")?;
    let n = s.len() / 4;
    let mut res = Vec::new();
    if n < 2 {
        return Ok(res);
    }
    let mut total = 0.0;
    for i in 0..n {
        total += (sq(s[4 * i + 2] - s[4 * i]) + sq(s[4 * i + 3] - s[4 * i + 1])).sqrt();
    }
    let cell = (total / n as f64).max(1e-6);
    let (mut ox, mut oy) = (f64::MAX, f64::MAX);
    for i in 0..n {
        ox = ox.min(s[4 * i].min(s[4 * i + 2]));
        oy = oy.min(s[4 * i + 1].min(s[4 * i + 3]));
    }
    // (celda, segmento) ordenado → cubetas contiguas
    let mut entries: Vec<(i64, u32)> = Vec::with_capacity(n * 2);
    for i in 0..n {
        let x0 = ((s[4 * i].min(s[4 * i + 2]) - ox) / cell - 1e-9).floor() as i64;
        let x1 = ((s[4 * i].max(s[4 * i + 2]) - ox) / cell + 1e-9).floor() as i64;
        let y0 = ((s[4 * i + 1].min(s[4 * i + 3]) - oy) / cell - 1e-9).floor() as i64;
        let y1 = ((s[4 * i + 1].max(s[4 * i + 3]) - oy) / cell + 1e-9).floor() as i64;
        for cx in x0..=x1 {
            for cy in y0..=y1 {
                entries.push(((cx << 32) ^ (cy & 0xFFFF_FFFF), i as u32));
            }
        }
    }
    entries.sort_unstable();
    let mut pairs: Vec<u64> = Vec::new();
    let mut a = 0;
    let mut blocks = 0usize;
    while a < entries.len() {
        let mut b = a + 1;
        while b < entries.len() && entries[b].0 == entries[a].0 {
            b += 1;
        }
        blocks += 1;
        if blocks & 0xFFF == 0 {
            c.check()?;
        }
        for x in a..b {
            for y in (x + 1)..b {
                let (i, j) = (entries[x].1.min(entries[y].1), entries[x].1.max(entries[y].1));
                pairs.push(((i as u64) << 32) | j as u64);
            }
        }
        a = b;
    }
    pairs.sort_unstable();
    pairs.dedup();
    for pr in pairs {
        let (i, j) = ((pr >> 32) as usize, (pr & 0xFFFF_FFFF) as usize);
        let k = classify(s, i, j, eps);
        if k != 0 {
            res.push((i as i32, j as i32, k));
        }
    }
    Ok(res)
}

// ---------------- grafos ----------------

pub fn components(nodes: usize, edges: &[i32], comp: &mut [i32], deg: &mut [i32]) -> PeResult<()> {
    if comp.len() != nodes || deg.len() != nodes {
        return Err(PeError::Invalid("búferes de salida".into()));
    }
    let mut uf = UnionFind::new(nodes);
    deg.iter_mut().for_each(|d| *d = 0);
    for (e, ab) in edges.chunks_exact(2).enumerate() {
        let (a, b) = (ab[0], ab[1]);
        if a < 0 || b < 0 || a as usize >= nodes || b as usize >= nodes {
            return Err(PeError::Invalid(format!("arista {e} con nodo fuera de rango")));
        }
        let (a, b) = (a as usize, b as usize);
        uf.union(a, b);
        deg[a] += 1;
        if b != a {
            deg[b] += 1;
        }
    }
    for i in 0..nodes {
        comp[i] = uf.find(i) as i32;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const NC: Cancel = Cancel(None);

    #[test]
    fn transform_identity_and_translation() {
        let p = [1.0, 2.0, 3.0, -4.0, 0.5, 0.0];
        let m = [1.0, 0.0, 0.0, 10.0, 0.0, 1.0, 0.0, -1.0, 0.0, 0.0, 1.0, 0.0];
        let mut o = [0.0; 6];
        transform3(&p, &m, &mut o, &NC).unwrap();
        assert_eq!(o, [11.0, 1.0, 3.0, 6.0, -0.5, 0.0]);
        assert!(transform3(&p, &[f64::NAN; 12], &mut o, &NC).is_err());
    }

    #[test]
    fn nearest_and_match_with_ties() {
        let r = [0.0, 0.0, 1.0, 0.0, 10.0, 10.0];
        let q = [0.5, 0.0, 9.9, 10.0, 50.0, 50.0];
        let mut i = [0; 3];
        let mut d = [0.0; 3];
        nearest(&q, &r, 2, 1.0, &mut i, &mut d, &NC).unwrap();
        assert_eq!(i, [0, 2, -1]); // empate 0.5/0.5 → menor índice
        let mut m = [0; 3];
        match_points(&q, &r, 2, 1.0, &mut m, &NC).unwrap();
        assert_eq!(m, [0, 2, -1]);
    }

    #[test]
    fn duplicates_transitive() {
        let p = [0.0, 0.0, 0.009, 0.0, 0.018, 0.0, 5.0, 5.0];
        let mut o = [0; 4];
        duplicates(&p, 2, 0.01, &mut o, &NC).unwrap();
        assert_eq!(o, [0, 0, 0, 3]);
    }

    #[test]
    fn segments_cross_touch_overlap() {
        let s = [0.0, 0.0, 2.0, 2.0, 0.0, 2.0, 2.0, 0.0, 2.0, 2.0, 3.0, 2.0, 1.0, 1.0, 1.5, 1.5, 10.0, 10.0, 11.0, 10.0];
        let r = segment_contacts(&s, 1e-7, &NC).unwrap();
        assert!(r.contains(&(0, 1, CROSS)));
        assert!(r.contains(&(0, 2, TOUCH)));
        assert!(r.contains(&(0, 3, OVERLAP)));
        assert!(!r.iter().any(|x| x.0 == 4 || x.1 == 4));
    }

    #[test]
    fn components_and_degree() {
        let mut c = [0; 5];
        let mut d = [0; 5];
        components(5, &[0, 1, 1, 2, 3, 4], &mut c, &mut d).unwrap();
        assert_eq!(c, [0, 0, 0, 3, 3]);
        assert_eq!(d, [1, 2, 1, 1, 1]);
        assert!(components(2, &[0, 5], &mut [0; 2], &mut [0; 2]).is_err());
    }

    #[test]
    fn cancellation_is_observed() {
        let f = AtomicBool::new(true);
        let p = vec![0.0; 2 * 10];
        let mut o = vec![0; 10];
        assert_eq!(duplicates(&p, 2, 0.1, &mut o, &Cancel(Some(&f))), Err(PeError::Cancelled));
    }
}
