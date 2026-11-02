//! fern_run/src/diagnostics.rs — residual & calibration diagnostics.
//!
//! Extracted verbatim from results.rs (the stats / conformal / calibration math):
//! normality tests (KS, Cramér–von Mises, Ljung–Box, Shapiro/AD battery), multivariate
//! joint normality (Mardia, Henze–Zirkler), p-value uniformity, EnbPI conformal
//! intervals, and whitened-residual calibration. results.rs keeps the artifact I/O +
//! SeqBundle; SeqBundle::diagnose* calls back into here.
//!
//! (statrs and normality `use` lines travel with the moved code below.)

use ndarray::{Array1, Array3}; // Array4 etc. are referenced fully-qualified (ndarray::Array4)
use rayon::prelude::*;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑦  Residual diagnostics — normality tests with rayon
//
// This replaces fr_result.py's residual_diagnostics(), the 200-line
// scipy/statsmodels function. The Rust version has two superpowers:
//
//   1. The `normality` crate gives us Shapiro-Wilk, Anderson-Darling,
//      Jarque-Bera, D'Agostino, and Lilliefors out of the box.
//
//   2. rayon gives us data parallelism with one word change:
//        .iter()     → .par_iter()
//      Each feature's tests run on a different core. No GIL.
//      No multiprocessing overhead. No data copying.
//
// Python bottleneck (sequential, GIL-locked):
//   for d in range(D):           # D features, one by one
//       for test in 8_tests:     # 8 tests per feature
//           run_test(resid[:, d])
//
// Rust + rayon (parallel across features):
//   residuals.par_iter()          # D features across N cores
//       .flat_map(|col| tests)    # 8 tests per feature, same core
//       .collect()                # gather results
//
// Scale comparison:
//   patch_normality_fdr on ETTh1 (D=321, T=720, patch=24):
//     Python: 9,630 tests × sequential = minutes
//     Rust:   9,630 tests ÷ 8 cores = seconds
//
//   titration sweep (10σ × 3 reps):
//     Python: 289,000 tests sequential = ~40 min
//     Rust:   289,000 tests parallel = ~5 min
//
// Cargo.toml dependencies for this pass:
//   normality = "0.4"    # Shapiro-Wilk, AD, JB, D'Agostino, Lilliefors
//   statrs = "0.18"      # χ² CDF for Ljung-Box, normal CDF for CvM
//   rayon = "1.10"        # par_iter()
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, serde::Serialize)]
pub struct TestResult {
    pub feature: String, // "x", "y", "z", "feat_0", ...
    pub test: String,    // "shapiro", "anderson", "jarque_bera", ...
    pub statistic: f64,  // the test statistic value
    pub p_value: f64,    // continuous p-value
    pub reject: bool,    // p_value < alpha
}

// ──────────────────────────────────────────────────────────────────────
// §8  Single-feature normality battery — the inner loop
//
// This function runs ALL normality tests on ONE feature's residuals.
// It's the unit of parallelism: rayon calls this once per feature,
// each on a different core.
//
// Python twin: the body of `for d, name in enumerate(cols):` in
// residual_diagnostics(). Same tests, same order, same semantics.
// ──────────────────────────────────────────────────────────────────────

// ──────────────────────────────────────────────────────────────────────
// §9  Hand-rolled tests — KS exact, Cramér-von Mises, Ljung-Box
//
// These three tests don't exist in the `normality` crate.
// But they're mathematically simple — each is ~20 lines of arithmetic
// on top of CDF functions from the `statrs` crate.
//
// Python used scipy.stats.kstest, scipy.stats.cramervonmises,
// statsmodels.acorr_ljungbox. We replicate the math directly.
// ──────────────────────────────────────────────────────────────────────

use statrs::distribution::{ChiSquared, ContinuousCDF, Normal};
//                          │            │             │
//                          │            │             └─ Normal distribution (CDF for KS, CvM)
//                          │            └─ Trait that gives .cdf() and .sf() methods
//                          └─ χ² distribution (p-value for Ljung-Box)
//
// statrs is Rust's equivalent of scipy.stats for distributions.
// It doesn't have the TEST functions (that's what `normality` crate does),
// but it has the DISTRIBUTIONS (CDFs, PDFs, quantiles) that tests need.

/// Kolmogorov-Smirnov test against N(0,1) with known σ.
/// residuals are divided by sigma first: z = r/σ ~ N(0,1) under the null.
/// Returns (statistic, p_value).
// CHG 2026-08-12: the D-max loop and asymptotic series moved to
// `ks_test_against_cdf` (the crate's one KS implementation); this keeps only
// the standardization that makes the normal comparison exact.
fn ks_test_exact(residuals: &[f64], sigma: f64) -> (f64, f64) {
    let normal = Normal::new(0.0, 1.0).unwrap();
    //                                  │
    //                                  └─ .unwrap(): Normal::new can fail if σ ≤ 0.
    //                                     We know σ = 1.0, so this is safe.

    // Standardize: z = r / σ
    let z: Vec<f64> = residuals.iter().map(|&r| r / sigma).collect();
    ks_test_against_cdf(&z, |x| normal.cdf(x))
}

/// Cramér-von Mises test against normal(μ, σ).
/// If sigma_known is Some, tests against N(0, σ). Otherwise estimates μ, σ.
/// Returns (statistic, p_value).
fn cramer_von_mises(residuals: &[f64], sigma_known: Option<f64>) -> (f64, f64) {
    let n = residuals.len() as f64;

    // Determine the reference normal distribution
    let (mu, sigma) = match sigma_known {
        Some(s) => (0.0, s),
        None => {
            let mean = residuals.iter().sum::<f64>() / n;
            let var = residuals.iter().map(|&x| (x - mean).powi(2)).sum::<f64>() / (n - 1.0);
            (mean, var.sqrt())
        }
    };
    let normal = Normal::new(mu, sigma).unwrap();

    // Sort the data
    let mut sorted: Vec<f64> = residuals.to_vec();
    sorted.sort_by(|a, b| a.partial_cmp(b).unwrap());

    // CvM statistic: W² = Σ[(F(x_i) - (2i-1)/(2n))²] + 1/(12n)
    let w2: f64 = sorted
        .iter()
        .enumerate()
        .map(|(i, &x)| {
            let f = normal.cdf(x);
            let term = f - (2.0 * (i as f64) + 1.0) / (2.0 * n);
            term * term
        })
        .sum::<f64>()
        + 1.0 / (12.0 * n);
    //
    // This is the textbook CvM formula.
    // Python: scipy.stats.cramervonmises computes the same thing.
    // The Rust version is an iterator chain — .map().sum() is
    // idiomatic Rust for "transform each element, add them up."

    // ── p-value: DOCKED — the statistic is verified, the p-value is NOT ────────────────
    // PARITY (2026-06-28): W² above matches scipy.stats.cramervonmises to ~1e-9 (verified).
    // The p-value below applies the Stephens (1976) ESTIMATED-parameter (case-3) correction
    // `w2*(1+0.5/n)` + the case-3 table UNCONDITIONALLY. That is WRONG when `sigma_known` is
    // Some: the CDF is then fully specified (case 0), and scipy's case-0 p-value differs by
    // ~6 orders (this returns ~7e-10 vs scipy's ~1e-3). The case-3 (None) path is also untested.
    // → Treat the CvM p-value as UNVERIFIED: the bridge uses the W² statistic and computes the
    //   p-value in Python (scipy), or a case-0 distribution must be implemented + verified here
    //   first. Left unchanged on purpose (no unverified case-0 port). See fern_run/PARITY.md.
    let w2_star = w2 * (1.0 + 0.5 / n);
    let p_value = cvm_p_value_normal_case3(w2_star);

    (w2, p_value)
}

/// CvM p-value from modified statistic (Stephens 1976 Table 4.9).
/// Linear interpolation between tabulated critical values.
fn cvm_p_value_normal_case3(z: f64) -> f64 {
    if z < 0.0275 {
        1.0 - (-13.953 + 775.5 * z - 12542.61 * z * z).exp()
    } else if z < 0.051 {
        1.0 - (-5.903 + 179.546 * z - 1515.29 * z * z).exp()
    } else if z < 0.092 {
        (0.886 - 31.62 * z + 10.897 * z * z).exp()
    } else if z < 1.1 {
        (1.111 - 34.242 * z + 12.832 * z * z).exp()
    } else {
        7.37e-10 // CHG: mirrors nortest lower clamp for very small p
    }
}

/*
EDF normality tests compare the empirical CDF of the sample against the
CDF of the reference distribution after sorting the observations.

For each ordered sample x_(i), compute u_i = F(x_(i)), where F is the
reference normal CDF. Under the null, the transformed values u_i should
behave like ordered Uniform(0,1) samples.

W² (Cramér-von Mises) measures the global squared mismatch between u_i and
their ideal midpoint locations (2i-1)/(2n). It is a balanced whole-shape
goodness-of-fit statistic.

U² (Watson) is a centered version of W²: it removes the average drift of
the u_i and is less sensitive to a uniform shift.

A² (Anderson-Darling) is a tail-weighted EDF statistic: it emphasizes
mismatch near 0 and 1, so it is more sensitive to tail misspecification.

Important: the null distribution depends on whether the normal parameters
are fully specified or estimated from the same sample. The Case-3 normality
correction applies only when mean and variance are estimated from the data.
*/

/// Ljung-Box test for autocorrelation at given max lag.
/// Tests H₀: residuals are iid (no autocorrelation up to lag k).
/// Returns (statistic, p_value).
fn ljung_box(residuals: &[f64], max_lag: usize) -> (f64, f64) {
    let n = residuals.len();
    let mean = residuals.iter().sum::<f64>() / n as f64;

    // Autocovariance at lag 0 (= variance)
    let gamma0: f64 = residuals.iter().map(|&x| (x - mean).powi(2)).sum::<f64>() / n as f64;
    if gamma0 < 1e-15 {
        return (0.0, 1.0); // constant residuals → no autocorrelation possible
    }

    // Ljung-Box statistic: Q = n(n+2) Σ_{k=1}^{K} r_k² / (n-k)
    // where r_k = sample autocorrelation at lag k.
    //
    // Intuition: if residuals are truly iid, r_k ≈ 0 for all k > 0.
    // Q sums up the squared autocorrelations, weighted by 1/(n-k).
    // The (n-k) correction accounts for fewer data pairs at higher lags.
    // Large Q = "these residuals remember their past" = not iid.

    let n_f = n as f64;
    let mut q = 0.0;
    let mut used_lags = 0usize;
    //                  │
    //                  └─ Track how many lags we actually computed.
    //                     If n = 8 and max_lag = 20, we can only compute
    //                     lags 1..7 (lag 8 needs pairs (x_1, x_9) which
    //                     don't exist). The χ² degrees of freedom must
    //                     match the number of lags USED, not REQUESTED.
    //
    //                     Bug in original: used max_lag for df regardless.
    //                     If max_lag > n, the p-value was computed against
    //                     a χ² distribution with too many df → p-value
    //                     too large → missed real autocorrelation.

    for lag in 1..=max_lag {
        if lag >= n {
            break;
        }
        //             │
        //             └─ Can't compute autocorrelation at lag k if k ≥ n.
        //                At lag k, you need pairs (x_t, x_{t+k}) for t = 1..n-k.
        //                If k ≥ n, there are zero pairs. Stop here.

        used_lags += 1;

        // Sample autocovariance at this lag:
        //   γ(k) = (1/n) Σ_{t=1}^{n-k} (x_t - x̄)(x_{t+k} - x̄)
        let gamma_k: f64 = residuals[..n - lag]
            .iter()
            .zip(&residuals[lag..])
            //     │
            //     └─ Pair up (x_t, x_{t+lag}) for all valid t.
            //        residuals[..n-lag] = [x_1, x_2, ..., x_{n-lag}]
            //        residuals[lag..]   = [x_{lag+1}, ..., x_n]
            //        zip pairs them: (x_1, x_{lag+1}), (x_2, x_{lag+2}), ...
            //
            //        Python: np.sum((r[:-lag] - mean) * (r[lag:] - mean)) / n
            .map(|(&a, &b)| (a - mean) * (b - mean))
            .sum::<f64>()
            / n_f;

        // Autocorrelation: r_k = γ(k) / γ(0)
        let r_k = gamma_k / gamma0;
        q += r_k * r_k / (n_f - lag as f64);
        //                  │
        //                  └─ The (n - k) denominator is Ljung-Box's
        //                     improvement over the original Box-Pierce test,
        //                     which just summed r_k². The correction gives
        //                     better χ² approximation for small samples.
    }
    q *= n_f * (n_f + 2.0);
    //   │
    //   └─ The n(n+2) prefactor. Final statistic:
    //      Q = n(n+2) Σ r_k² / (n-k)
    //
    //      Under H₀ (iid residuals), Q ~ χ²(K) where K = used_lags.

    if used_lags == 0 {
        return (0.0, 1.0);
        //  │
        //  └─ Edge case: n ≤ 1, no lags computable.
        //     Return "no evidence of autocorrelation."
    }

    // p-value: P(χ²(K) > Q)
    let chi2 = ChiSquared::new(used_lags as f64).unwrap();
    //                          │
    //                          └─ used_lags, NOT max_lag.
    //                             df must equal the number of terms in the sum.
    //                             If we requested 10 lags but only computed 7,
    //                             the statistic is a sum of 7 terms and follows
    //                             χ²(7), not χ²(10).

    let p_value = chi2.sf(q);
    //                 │
    //                 └─ .sf(x) = survival function = P(X > x) = 1 - CDF(x).
    //                    Upper tail: "what's the probability of seeing a Q
    //                    this large or larger under H₀?"
    //                    Small p → reject H₀ → residuals are autocorrelated.

    (q, p_value)
}

fn normality_battery(
    residuals: &[f64],
    //          └─ &[f64]: a borrowed slice of floats.
    //             This is one column of the residual matrix: [T] values.
    //             Borrowed because rayon shares immutable data across threads
    //             without copying. Each thread reads its own column.
    //
    //             Python: resid[:, d] — a numpy view, also zero-copy.
    //             The semantics are the same: read-only access to column data.
    feature_name: &str,
    alpha: f64,
    sigma_known: Option<f64>,
    //           │
    //           └─ Some(σ) for known-σ path (deterministic ODEs with injected noise).
    //              None for estimated-σ path (stochastic systems).
    //              This controls which tests run, same as the Python if/else.
) -> Vec<TestResult> {
    let mut results = Vec::new();
    let name = feature_name.to_string();

    // ── Normality tests from the `normality` crate ──
    //
    // Each test takes a &[f64] and returns a NormalityTestResult
    // with { statistic, p_value, is_normal(alpha) }.
    //
    // The crate handles all the internal math:
    //   Shapiro-Wilk:    coefficient tables + regression on order statistics
    //   Anderson-Darling: EDF statistic + Stephens' correction
    //   Jarque-Bera:     skewness² + kurtosis² vs χ²(2)
    //   D'Agostino:      skewness transform → z-score
    //   Lilliefors:      KS statistic with Monte Carlo p-value tables
    //
    // Python used scipy + statsmodels. Rust uses one crate.

    // Shapiro-Wilk (the gold standard for normality, sample ≤ 5000)
    let sample = if residuals.len() > 5000 {
        &residuals[..5000]
        //  │
        //  └─ Slice syntax: &data[..5000] = "first 5000 elements."
        //     Same as Python's resid[:5000].
        //     Shapiro-Wilk has a sample size limit (algorithm uses
        //     precomputed coefficients up to n=5000).
    } else {
        residuals
    };

    if let Ok(sw) = normality::shapiro_wilk(sample.iter().copied()) {
        //  │           │
        //  │           └─ normality::shapiro_wilk(&[f64]) → Result<NormalityTestResult, Error>
        //  │              Returns Ok if computation succeeds, Err if data is degenerate
        //  │              (all zeros, too few points, etc.)
        /*
        Think about what sample is and what each step does:
        rustsample: &[f64]          // a borrowed slice of floats
        .iter() creates an iterator that yields references to each element:
        rustsample.iter()            // yields: &f64, &f64, &f64, ...
                                // "here's where each number LIVES in memory"
        .copied() dereferences each one — copies the 8-byte number out:
        rustsample.iter().copied()   // yields: f64, f64, f64, ...
                                // "here's each number ITSELF"
        The distinction: &f64 is a pointer to a number (8 bytes of address).
        f64 is the number itself (8 bytes of value).
        For something this small, copying the value is actually cheaper than chasing a pointer.
        That's why Rust doesn't auto-dereference here — it lets you choose.
        The normality crate's function signature says "give me an iterator of f64 values"
        — it wants the numbers themselves, not pointers to them.
        Your slice can only give &f64 references. .copied() bridges the gap.
        You'll see a similar adapter .cloned() — same idea but for heap types like String
        where copying is expensive. .copied() only works on Copy types (numbers, bools, small things).
        It's the compiler saying "yes, this copy is cheap, go ahead."
         */
        //  │
        //  └─ `if let Ok(sw)`: "if the test succeeded, use the result."
        //     If it failed (degenerate data), skip silently.
        //     Python: try/except around each test, same idea.

        results.push(TestResult {
            feature: name.clone(),
            //            │
            //            └─ .clone(): we reuse `name` for every test.
            //               Each TestResult owns its own String.
            //               Python: dicts share the string reference (refcounted).
            //               Rust: each struct gets an owned copy.
            //               For short feature names, this is negligible.
            test: "shapiro".into(),
            statistic: sw.statistic,
            p_value: sw.p_value,
            reject: sw.p_value < alpha,
        });
    }

    // Anderson-Darling (better tail sensitivity than Shapiro-Wilk)
    if let Ok(ad) = normality::anderson_darling(residuals.iter().copied()) {
        results.push(TestResult {
            feature: name.clone(),
            test: "anderson".into(),
            statistic: ad.statistic,
            p_value: ad.p_value,
            reject: ad.p_value < alpha,
        });
    }

    // Jarque-Bera (normality via 3rd and 4th moments)
    // Only on estimated-σ path — known-σ path uses exact KS instead
    if sigma_known.is_none() {
        if let Ok(jb) = normality::jarque_bera(residuals.iter().copied()) {
            results.push(TestResult {
                feature: name.clone(),
                test: "jarque_bera".into(),
                statistic: jb.statistic,
                p_value: jb.p_value,
                reject: jb.p_value < alpha,
            });
        }
    }

    // Lilliefors (KS with estimated parameters)
    // Only on estimated-σ path — known-σ path uses exact KS
    if sigma_known.is_none() {
        if let Ok(lf) = normality::lilliefors(residuals.iter().copied()) {
            results.push(TestResult {
                feature: name.clone(),
                test: "lilliefors".into(),
                statistic: lf.statistic,
                p_value: lf.p_value,
                reject: lf.p_value < alpha,
            });
        }
    }

    // KS exact (known-σ path only: z = r/σ ~ N(0,1), no parameter estimation)
    if let Some(sigma) = sigma_known {
        let ks = ks_test_exact(residuals, sigma);
        results.push(TestResult {
            feature: name.clone(),
            test: "ks".into(),
            statistic: ks.0,
            p_value: ks.1,
            reject: ks.1 < alpha,
        });
    }

    // Cramér-von Mises
    let cvm = cramer_von_mises(residuals, sigma_known);
    results.push(TestResult {
        feature: name.clone(),
        test: "cvm".into(),
        statistic: cvm.0,
        p_value: cvm.1,
        reject: cvm.1 < alpha,
    });

    // ── iid / temporal structure tests ──
    // These run on BOTH paths (known-σ and estimated-σ).
    // They check whether residuals are independent, not just normal.

    // Ljung-Box (autocorrelation at lag 10)
    let lb = ljung_box(residuals, 10);
    results.push(TestResult {
        feature: name.clone(),
        test: "ljung_box".into(),
        statistic: lb.0,
        p_value: lb.1,
        reject: lb.1 < alpha,
    });

    results
}

// ──────────────────────────────────────────────────────────────────────
// §10  residual_diagnostics — the public API (parallel)
//
// This is the Rust replacement for fr_result.py's residual_diagnostics().
// Same inputs, same outputs, same test battery — but parallel.
//
// Input:  residual matrix [T, D] as column-major Vec<Vec<f64>>
//         + feature names + alpha + optional known sigma
// Output: Vec<TestResult> (one per test per feature)
//
// The .par_iter() call distributes features across CPU cores.
// ──────────────────────────────────────────────────────────────────────

pub fn residual_diagnostics(
    residuals: &[Vec<f64>], // [T][D] — each inner Vec = one timestep
    //          │
    //          └─ &[Vec<f64>]: a slice of columns.
    feature_names: &[String],
    //              │
    //              └─ &[String]: borrowed slice of feature names.
    //                 ["x", "y", "z"] for Lorenz, ["feat_0", ..., "feat_320"] for ETTh1.
    alpha: f64,
    sigma_known: Option<f64>,
) -> Vec<TestResult> {
    /*
    "For each (column, name) pair, run the battery, flatten the results."

    # Python: sequential, same structure
    results = []
    for col, name in zip(residuals.T, feature_names):
        results.extend(normality_battery(col, name, alpha, sigma))

    Now map it one-to-one:

    Python                          Rust
    ─────────────────────────       ─────────────────────────
    for col, name in                .par_iter()
        zip(residuals.T,                .zip(feature_names.par_iter())
            feature_names):
        results.extend(                 .flat_map(|(col, name)| {
            battery(col, name))             battery(col, name)
                                        })
    results                             .collect()

    flat_map = map + flatten. It says "each item produces a Vec, unwrap all of them into one stream."
    Then .collect() gathers that stream into the final Vec.
    If you used .map() instead, you'd get a Vec<Vec<TestResult>>
    — a list of lists. flat_map peels off that inner layer, same as .extend() does in the Python loop.
    */
    let d = feature_names.len();

    // Internal transpose: [T][D] → D columns of [T]
    // Each column becomes an independent slice for rayon.
    // This allocation is tiny: D vectors of T floats.
    // For ETTh1 (T=720, D=321): ~1.8 MB. Dwarfed by the test computation.

    let columns: Vec<Vec<f64>> = (0..d)
        .map(|col| residuals.iter().map(|row| row[col]).collect())
        .collect();

    columns
        .par_iter()
        //  │
        //  └─ .par_iter(): THIS IS THE MAGIC LINE.
        //     rayon's parallel iterator. Same API as .iter(), but
        //     distributes work across all available CPU cores.
        //
        //     Without rayon: .iter()     → sequential, one core
        //     With rayon:    .par_iter() → parallel, all cores
        //
        //     That's it. One word. No thread pool setup, no channel
        //     plumbing, no Arc<Mutex<>>, no spawn/join. rayon handles
        //     everything: work stealing, load balancing, thread count.
        //
        //     Python can't do this. The GIL prevents true parallel
        //     execution of Python code. multiprocessing.Pool works but
        //     copies data between processes (expensive for large arrays).
        //     rayon shares data via immutable references (zero-copy).
        .zip(feature_names.par_iter())
        //    │
        //    └─ .zip(): pair each column with its feature name.
        //       Both iterators advance in parallel.
        //       Python: zip(residuals.T, feature_names) in the for loop.
        //       Rust: same semantics, but parallel.
        .flat_map(|(col, name)| {
            //  │       │
            //  │       └─ (col, name): one column of residuals + its feature name.
            //  │          col: &Vec<f64> — one feature's [T] residuals
            //  │          name: &String — "x", "y", "z", etc.
            //  │
            //  └─ .flat_map(): run the battery on each column, flatten all
            //     Vec<TestResult> into one big iterator.
            //
            //     .map() would give Vec<Vec<TestResult>> (nested).
            //     .flat_map() gives Vec<TestResult> (flattened).
            //     Python: list comprehension with nested loop:
            //       [test for col in cols for test in run_battery(col)]

            normality_battery(col, name, alpha, sigma_known)
        })
        .collect()
    //  │
    //  └─ .collect(): gather all TestResults into a single Vec.
    //     rayon's collect is thread-safe — each thread accumulates
    //     its own Vec, then they're merged at the end. No locks.
    //
    //     The result: Vec<TestResult> with D × (num_tests) entries.
    //     For Lorenz (D=3): ~3 × 8 = 24 entries.
    //     For ETTh1 (D=321): ~321 × 8 = 2,568 entries.
    //     The parallel version produces them in arbitrary order
    //     (whichever core finishes first). Sort by feature if you
    //     need deterministic order.
}

// ──────────────────────────────────────────────────────────────────────
// §11  patch_normality_fdr — per-window tests with FDR correction
//
// Slices residuals into non-overlapping windows of patch_size,
// runs Shapiro-Wilk per window per feature, applies BH-FDR.
//
// This is where the parallelism pays off most:
//   n_windows × D = potentially thousands of independent tests.
//
// Python twin: patch_normality_fdr() in fr_result.py
// ──────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, serde::Serialize)]
pub struct PatchTestResult {
    pub window: usize,
    pub feature: String,
    pub statistic: f64,
    pub p_value: f64,
    pub p_corrected: f64, // BH-FDR adjusted
    pub reject: bool,     // p_corrected < alpha
}

pub fn patch_normality_fdr(
    residuals: &[Vec<f64>], // [D][T] column-major
    feature_names: &[String],
    patch_size: usize,
    alpha: f64,
) -> (f64, Vec<PatchTestResult>) {
    //  │    │
    //  │    └─ Detailed per-window results
    //  └─ Overall rejection rate (float in [0, 1])

    let t_len = residuals[0].len();
    let n_windows = t_len / patch_size;

    // Generate all (window, feature) jobs
    let jobs: Vec<(usize, usize)> = (0..n_windows)
        .flat_map(|w| (0..residuals.len()).map(move |d| (w, d)))
        .collect();
    //  │
    //  └─ Cartesian product of windows × features.
    //     For T=720, D=321, patch=24: 30 × 321 = 9,630 jobs.
    //     Each job is independent — perfect for parallelism.

    // Run all Shapiro-Wilk tests in parallel
    let raw_results: Vec<(usize, usize, f64, f64)> = jobs
        .par_iter()
        //  │
        //  └─ .par_iter() on the 9,630 jobs. rayon splits them
        //     across cores. Each core processes ~1,200 tests.
        .filter_map(|&(w, d)| {
            let start = w * patch_size;
            let end = start + patch_size;
            let chunk = &residuals[d][start..end];
            //                        │
            //                        └─ Slice into the window.
            //                           No allocation — just a view into the Vec.

            normality::shapiro_wilk(chunk.iter().copied())
                .ok()
                .map(|sw| (w, d, sw.statistic, sw.p_value))
        })
        .collect();

    // ── Benjamini-Hochberg FDR correction ──
    //
    // Sort p-values ascending, compute adjusted p-values:
    //   p_adjusted[i] = min(p[i] * m / rank[i], 1.0)
    // where m = total number of tests.
    //
    // Python used statsmodels.stats.multitest.multipletests.
    // The BH procedure is simple enough to inline.

    // CHG 2026-08-12: the BH arithmetic moved to `benjamini_hochberg_adjusted`
    // — the crate's ONE implementation. This site used to reject with strict
    // `<`; the canonical Benjamini-Hochberg rule is `<=` in both of its
    // formulations, so rejection here changes only when an adjusted p-value
    // equals alpha EXACTLY (a measure-zero boundary).
    let p_corrected = benjamini_hochberg_adjusted(&raw_results.iter().map(|&(_, _, _, p)| p).collect::<Vec<_>>());

    // Build output
    let mut output: Vec<PatchTestResult> = raw_results
        .iter()
        .enumerate()
        .map(|(i, &(w, d, stat, p))| PatchTestResult {
            window: w,
            feature: feature_names[d].clone(),
            statistic: stat,
            p_value: p,
            p_corrected: p_corrected[i],
            reject: p_corrected[i] <= alpha,
        })
        .collect();

    let rejection_rate = output.iter().filter(|r| r.reject).count() as f64 / raw_results.len() as f64;

    output.sort_by_key(|r| (r.window, r.feature.clone()));

    (rejection_rate, output)
}

// ──────────────────────────────────────────────────────────────────────
// §12  titration_diagnostics — sweep across noise levels
//
// This is the "big picture" function. The paper's central claim is:
//   "Fern recovers the target distribution under increasing noise."
//
// To test that, you run Fern at σ = 0, 0.25, 0.5, 1.0, 2.0 and check:
//   "Do the residuals stay Gaussian as noise increases?"
//
// Each noise level produces a full diagnostic battery (§10).
// This function runs ALL noise levels and summarizes:
//   "For each test, what fraction of features rejected normality?"
//
// A good model: rejection_rate stays near α (the false positive rate)
//   across all σ. The residuals look like noise at every level.
//
// A bad model: rejection_rate spikes at high σ. The model can't
//   absorb the noise — residuals become structured (non-Gaussian).
//
// The output is a table like:
//   noise_sigma | test      | rejection_rate
//   0.00        | shapiro   | 0.00    ← clean data, model fits perfectly
//   0.25        | shapiro   | 0.05    ← near α, expected false positives
//   1.00        | shapiro   | 0.08    ← still near α, model absorbs noise
//   2.00        | shapiro   | 0.60    ← spike! model can't handle this σ
//
// Python twin: titration_diagnostics() in fr_result.py
// The Python version was sequential: one σ at a time, each σ runs
// D features sequentially. Double sequential loop.
// Rust: outer par_iter over σ levels, inner par_iter over features.
// Both loops parallel. rayon nests correctly — one shared thread pool.
// ──────────────────────────────────────────────────────────────────────

struct RejectTally {
    rejects: usize, // how many features rejected normality
    total: usize,   // how many features were tested
}

impl RejectTally {
    fn new() -> Self {
        Self { rejects: 0, total: 0 }
    }

    fn record(&mut self, rejected: bool) {
        //        │
        //        └─ &mut self: we're modifying the tally.
        //           The caller must hold a mutable reference.
        //           Python: just self.rejects += 1 (always mutable).
        //           Rust: mutation is opt-in and visible in the signature.

        if rejected {
            self.rejects += 1;
        }
        self.total += 1;
    }

    fn rate(&self) -> f64 {
        //      │
        //      └─ &self: read-only. Computing the ratio doesn't change the tally.

        if self.total == 0 {
            return 0.0;
        }
        //  │
        //  └─ Guard against division by zero.
        //     Shouldn't happen in practice (we always have at least one feature),
        //     but defensive code > mysterious NaN in the output.

        self.rejects as f64 / self.total as f64
        //             │
        //             └─ Integer → float conversion. 2 / 3 = 0 in integer math.
        //                2.0 / 3.0 = 0.667. Rust requires explicit `as f64`.
        //                Python 3 auto-floats division: 2 / 3 = 0.667.
    }
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct TitrationRow {
    pub noise_sigma: f64,
    //               │
    //               └─ Which noise level this row summarizes.
    //                  0.0, 0.25, 0.5, 1.0, 2.0, etc.
    //                  Maps to the σ in your titration sweep:
    //                    results/lorenz_fern_s42_p336_σ1e-01/
    //                    results/lorenz_fern_s42_p336_σ1e+00/
    pub test: String,
    //        │
    //        └─ Which normality test: "shapiro", "anderson", etc.
    //           Each σ × test combination gets one row.
    //           If you have 5 σ levels × 6 tests = 30 TitrationRows.
    pub rejection_rate: f64,
    //                  │
    //                  └─ Fraction of features where this test rejected
    //                     normality at this noise level. Range [0.0, 1.0].
    //
    //                     Computed as: (# features with p < α) / (# features)
    //
    //                     For Lorenz (D=3): each test checks 3 features.
    //                       0/3 rejected → 0.00
    //                       1/3 rejected → 0.33
    //                       3/3 rejected → 1.00
    //
    //                     For ETTh1 (D=321): each test checks 321 features.
    //                       16/321 rejected → 0.05 (≈ α, expected under H₀)
    //                       200/321 rejected → 0.62 (model failing)
    //
    //                     The paper plots rejection_rate vs noise_sigma,
    //                     one curve per test. A flat line near α = good.
    //                     A rising curve = model breaks down at that σ.
}

pub fn titration_diagnostics(
    runs: &[(f64, Vec<Vec<f64>>, Vec<String>)],
    //      │
    //      └─ Each tuple is one noise level:
    //           .0 = noise_sigma
    //           .1 = residuals [D][T] (D columns of T residuals)
    //           .2 = feature_names
    //
    //         Typically 5-10 entries, one per σ level in the sweep.
    alpha: f64,
) -> Vec<TitrationRow> {
    runs.par_iter()
        //   │
        //   └─ Outer parallelism: each σ level on a different core.
        //      Inside each, residual_diagnostics uses its own par_iter
        //      across features. rayon nests correctly — one thread pool,
        //      work-stealing distributes inner tasks to idle threads.
        .flat_map(|(sigma, residuals, names)| {
            let sigma_opt = if *sigma > 0.0 { Some(*sigma) } else { None };
            //                  │
            //                  └─ *sigma: dereference. par_iter yields &f64,
            //                     we need f64. The * copies the 8-byte number.

            let diag = residual_diagnostics(residuals, names, alpha, sigma_opt);
            //         │
            //         └─ Full diagnostic battery at this σ level.
            //            Returns Vec<TestResult>: one per (feature, test).

            // ── Group by test name, compute rejection rate ──

            let mut by_test: std::collections::HashMap<String, RejectTally> = std::collections::HashMap::new();

            for r in &diag {
                by_test
                    .entry(r.test.clone())
                    //     │
                    //     └─ .entry(key): look up this test name in the map.
                    //        Returns an Entry that lets you inspect or insert.
                    //        Python: d.setdefault(key, default)
                    .or_insert_with(RejectTally::new)
                    //               │
                    //               └─ If this test name isn't in the map yet,
                    //                  create a fresh RejectTally { rejects: 0, total: 0 }.
                    //                  .or_insert_with(f) calls f only when needed (lazy).
                    //                  Python: d.setdefault("shapiro", RejectTally())
                    .record(r.reject);
                //       │
                //       └─ Increment the tally. If r.reject is true,
                //          both rejects and total go up. If false,
                //          only total goes up.
                //
                //          Compare to the old tuple version:
                //            entry.0 += if r.reject { 1 } else { 0 };
                //            entry.1 += 1;
                //          Which was .0 again? rejects or total?
                //          With the struct: tally.record(rejected) — no ambiguity.
            }

            by_test
                .into_iter()
                //  │
                //  └─ Consume the HashMap, yield (test_name, tally) pairs.
                .map(|(test, tally)| TitrationRow {
                    noise_sigma: *sigma,
                    test,
                    rejection_rate: tally.rate(),
                    //              │
                    //              └─ The whole reason RejectTally exists:
                    //                 this line says WHAT it computes, not HOW.
                    //                 tally.rate() vs (entry.0 as f64 / entry.1 as f64)
                })
                .collect::<Vec<_>>()
        })
        .collect()
    //  │
    //  └─ All σ levels' rows flattened into one Vec<TitrationRow>.
    //     Order is nondeterministic (par_iter). Sort by
    //     (noise_sigma, test) if deterministic output needed.
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑧  Multivariate normality — Mardia + Henze-Zirkler
//
// Everything in passes ⑦ was UNIVARIATE: one feature at a time.
// "Is r_x Gaussian? Is r_y Gaussian? Is r_z Gaussian?"
// But marginal normality ≠ joint normality.
//
// Classic counterexample: distribute points uniformly on a circle in R².
// Project onto x-axis → Gaussian. Project onto y-axis → Gaussian.
// But the joint distribution is a ring, not a Gaussian blob.
// Every univariate test passes. The data is wildly non-Gaussian.
//
// For Fern this matters directly. The Brenier map theory says the
// learned transport pushes the DATA distribution (joint, D-dimensional)
// toward the target. The guarantee is about the JOINT r = (r_1, ..., r_D),
// not the marginals separately. If you only check marginals, you might
// miss cross-channel artifacts the transport map introduced.
//
// The `normality` crate provides two multivariate tests:
//
//   Mardia's test: two statistics
//     • Skewness: b_{1,p} based on cubed Mahalanobis distances.
//       Catches asymmetric departures in any direction.
//       Under MVN: (n/6) * b_{1,p} ~ χ²(p(p+1)(p+2)/6)
//
//     • Kurtosis: b_{2,p} based on squared Mahalanobis distances.
//       Under MVN, each Mahalanobis distance² ~ χ²(D), so b_{2,p}
//       should equal D(D+2). Catches heavy/light tails jointly.
//       Under MVN: (b_{2,p} - D(D+2)) / sqrt(8D(D+2)/n) ~ N(0,1)
//
//   Henze-Zirkler (HZ): omnibus, consistent against ALL alternatives.
//     Measures L² distance between the empirical characteristic function
//     and the MVN characteristic function. Under MVN, HZ is approximately
//     log-normally distributed.
//
// For the Fern paper, the finding would be:
//   "Marginal normality holds per channel (univariate tests) AND
//    joint normality holds across channels (Mardia/HZ) — confirming
//    the Brenier map recovers the target distribution jointly,
//    not just marginally, despite channel-independent training."
//
// Cargo.toml: normality crate already includes the multivariate module.
//   use normality::multivariate::{mardia, henze_zirkler, HenzeZirklerMethod};
//
// Python: had NO multivariate normality tests. This is NEW capability.
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

use normality::multivariate::{HenzeZirklerMethod, MardiaMethod, henze_zirkler, mardia};
//                             │       │              │
//                             │       │              └─ Enum: LogNormal or Bootstrap.
//                             │       │                 LogNormal = asymptotic p-value (fast).
//                             │       │                 Bootstrap = resampled p-value (slow, more accurate for small n).
//                             │       │                 Use LogNormal for routine diagnostics,
//                             │       │                 Bootstrap for paper-quality results.
//                             │       │
//                             │       └─ Henze-Zirkler: omnibus MVN test.
//                             │          Consistent against all alternatives.
//                             │          Recommended by Thode (2002) as the best
//                             │          general-purpose MVN test.
//                             │
//                             └─ Mardia: skewness + kurtosis MVN test.
//                                Returns TWO results (skew and kurtosis).
//                                More interpretable than HZ: you know WHETHER
//                                it's asymmetry or tail thickness causing rejection.

// ──────────────────────────────────────────────────────────────────────
// §13  MultivarTestResult — output of joint normality tests
// ──────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, serde::Serialize)]
pub struct MultivarTestResult {
    pub test: String,
    //        │
    //        └─ "mardia_skewness", "mardia_kurtosis", or "henze_zirkler"
    pub statistic: f64,
    pub p_value: f64,
    pub reject: bool,
}

// ──────────────────────────────────────────────────────────────────────
// §14  joint_normality — the multivariate diagnostic API
//
// Input:  residual matrix as Vec<Vec<f64>> — each inner Vec is one
//         OBSERVATION (one time step), containing D feature values.
//         Shape: [T][D] — T observations of D-dimensional vectors.
//
//         ⚠ NOTE: this is ROW-major (each inner Vec = one time step),
//         which is DIFFERENT from residual_diagnostics (§10) which
//         uses column-major (each inner Vec = one feature's time series).
//
//         Why the difference? The normality crate's multivariate API
//         expects data as "list of observation vectors":
//           vec![
//               vec![x_1, y_1, z_1],   // observation 1
//               vec![x_2, y_2, z_2],   // observation 2
//               ...
//           ]
//         This is the standard multivariate statistics convention:
//         n observations of p-dimensional vectors.
//
// Output: Vec<MultivarTestResult> — typically 3 entries
//         (Mardia skewness, Mardia kurtosis, Henze-Zirkler)
//
// Python twin: NONE. This is new. Your Python diagnostics only
// tested marginals. This tests the joint distribution.
// ──────────────────────────────────────────────────────────────────────

pub fn joint_normality(
    observations: Vec<Vec<f64>>,
    //            │
    //            └─ Vec<Vec<f64>>: owned, not borrowed.
    //               The normality crate takes ownership of the data
    //               (it needs to sort/transform internally).
    //               The caller should .clone() if they need the data after.
    //
    //               For T=720, D=3: 720 vectors of 3 floats = ~17 KB.
    //               Cloning is negligible.
    alpha: f64,
    use_bootstrap: bool,
    //              │
    //              └─ false = LogNormal asymptotic p-value (fast, good for n > 50)
    //                 true  = Bootstrap resampled p-value (slow, better for small n)
    //
    //                 For Fern with T=336 or T=720: use false (asymptotic).
    //                 For debugging with tiny test sets: use true.
) -> Vec<MultivarTestResult> {
    let mut results = Vec::new();

    // ── Mardia's test ──
    //
    // Returns two statistics: skewness and kurtosis.
    // Both must pass for MVN to hold.
    //
    // The crate's mardia() returns a struct with fields:
    //   .skewness, .skewness_statistic, .skewness_p_value
    //   .kurtosis, .kurtosis_statistic, .kurtosis_p_value
    //
    // Interpretation:
    //   Skewness rejected → residuals are asymmetric in some joint direction.
    //     "The Brenier map has a directional bias."
    //   Kurtosis rejected → residuals have wrong tail thickness jointly.
    //     "The Brenier map produces too many/few extreme joint deviations."
    //   Both pass → the D-dimensional residual cloud looks Gaussian.

    // CHG (parity 2026-06-28): flipped `false`→`true` and corrected the comment, which had
    // the flag backwards. In `normality` 3.0.1, `true` = POPULATION covariance (1/n) — the
    // standard Mardia/MVN convention and what pingouin uses; the parity tests match pingouin
    // to ~1e-12 with `true`. The old `false` is SAMPLE covariance (1/(n-1)), which diverged
    // ~1e-2 from pingouin at T=40 (not "negligible"). See fern_run/PARITY.md.
    if let Ok(m) = mardia(observations.clone(), true, MardiaMethod::Asymptotic) {
        //                     │                   │
        //                     │                   └─ `true` = population covariance (1/n); see CHG above.
        //                     │
        //                     └─ .clone(): mardia takes ownership. We need the data again
        //                        for henze_zirkler below. Clone here, consume there.

        results.push(MultivarTestResult {
            test: "mardia_skewness".into(),
            statistic: m.skewness.statistic,
            //         │
            //         └─ The (n/6) * b_{1,p} test statistic.
            //            Under MVN: χ²(p(p+1)(p+2)/6).
            //            For D=3: χ²(10). For D=7: χ²(84).
            p_value: m.skewness.p_value,
            reject: m.skewness.p_value < alpha,
        });

        results.push(MultivarTestResult {
            test: "mardia_kurtosis".into(),
            statistic: m.kurtosis.statistic,
            //         │
            //         └─ The standardized kurtosis: (b_{2,p} - D(D+2)) / sqrt(8D(D+2)/n).
            //            Under MVN: approximately N(0,1).
            p_value: m.kurtosis.p_value,
            reject: m.kurtosis.p_value < alpha,
        });
    }

    // ── Henze-Zirkler test ──
    //
    // The omnibus MVN test. Consistent against ALL alternatives:
    // any departure from MVN is detected given enough data.
    // Mardia can miss departures that don't show up in skewness/kurtosis
    // (e.g., multimodality with symmetric modes). HZ catches those.
    //
    // The test statistic measures the L² distance between the
    // empirical characteristic function and the Gaussian one.
    // Under MVN, HZ is approximately log-normally distributed.

    let hz_method = if use_bootstrap {
        HenzeZirklerMethod::MonteCarlo(1000)
    } else {
        HenzeZirklerMethod::LogNormal
        //                  │
        //                  └─ Asymptotic: uses the log-normal approximation
        //                     for the null distribution of HZ.
        //                     Fast (no resampling), accurate for n > 50.
    };

    // CHG (parity 2026-06-28): `false`→`true` = population covariance (1/n), matching Mardia
    // above and pingouin (verified to ~1e-12). See fern_run/PARITY.md.
    if let Ok(hz) = henze_zirkler(observations, true, hz_method) {
        //                             │           │
        //                             │           └─ true = population covariance (matches Mardia + pingouin)
        //                             │
        //                             └─ observations consumed here (no clone needed —
        //                                this is the last use of the data).

        results.push(MultivarTestResult {
            test: "henze_zirkler".into(),
            statistic: hz.statistic,
            p_value: hz.p_value,
            reject: hz.p_value < alpha,
        });
    }

    results

    // Typical output for Lorenz (D=3, T=720, well-fitted Fern):
    //   [
    //     { test: "mardia_skewness",  statistic: 4.2,  p_value: 0.64, reject: false },
    //     { test: "mardia_kurtosis",  statistic: -0.8, p_value: 0.42, reject: false },
    //     { test: "henze_zirkler",    statistic: 0.9,  p_value: 0.35, reject: false },
    //   ]
    //
    // All three pass → "the D-dimensional residual distribution is
    // consistent with multivariate normality." This is the Brenier claim.
    //
    // Dimension constraint: Mardia and HZ both invert the sample
    // covariance matrix S (D×D). For D > T (more features than time steps),
    // S is singular. The crate may return Err in that case.
    // For large D (e.g., D=321 ETTh1), options:
    //   a) Test on top-k PCA components instead of all D
    //   b) Use Ledoit-Wolf shrinkage on S before testing
    //   c) Test pairwise: all (D choose 2) bivariate joint tests
    // For the paper, Lorenz (D=3) and Rössler (D=3) are the main targets.
    // ETTh1 multivariate testing is a stretch goal.
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑨  p-value uniformity — the meta-diagnostic
//
// If the model is well-specified, p-values under the null are U(0,1).
// This is a theorem, not an assumption: if H₀ is true and the test
// is exact, the p-value is a uniform random variable.
//
// So we can TEST the p-values themselves: "are they uniform?"
//
// Method: probit transform. If p ~ U(0,1), then Φ⁻¹(p) ~ N(0,1).
// (Φ⁻¹ is the inverse standard normal CDF, aka the probit function.)
// After transforming, run Anderson-Darling for normality on the z-scores.
//
// If AD rejects → the p-values aren't uniform → the model is
// mis-specified in a way that goes beyond what individual tests show.
//
// This is a "test of tests" — it aggregates signal across features.
// Individual per-feature tests might each be borderline (p ≈ 0.06),
// but if ALL features are borderline, the aggregate is damning.
// pvalue_uniformity catches that pattern.
//
// Python twin: pvalue_uniformity() in fr_result.py — same math.
// Rust version uses the normality crate's anderson_darling for the
// final check (same test, same implementation, no hand-rolling).
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ──────────────────────────────────────────────────────────────────────
// §15  PvalueUniformityResult — one row per test name
// ──────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, serde::Serialize)]
pub struct PvalueUniformityResult {
    pub test: String,
    //        │
    //        └─ Which test's p-values we're examining.
    //           "shapiro", "anderson", etc.
    pub ad_statistic: f64,
    //                │
    //                └─ Anderson-Darling statistic on the probit-transformed p-values.
    //                   Large value → p-values deviate from uniform → model suspect.
    pub p_value_of_pvalues: f64,
    //                      │
    //                      └─ The meta p-value. If this is small (< 0.05),
    //                         the p-values from this test are NOT uniform,
    //                         meaning the model is mis-specified.
    //                         "The test says normality holds per-feature,
    //                          but the pattern across features says it doesn't."
}

// ──────────────────────────────────────────────────────────────────────
// §16  pvalue_uniformity — the implementation
//
// Input:  Vec<TestResult> from residual_diagnostics (§10)
//         Optional: filter to one test name
// Output: Vec<PvalueUniformityResult>, one per unique test name
// ──────────────────────────────────────────────────────────────────────

pub fn pvalue_uniformity(
    diagnostics: &[TestResult],
    //            │
    //            └─ The output of residual_diagnostics.
    //               Each entry has: feature, test, statistic, p_value, reject.
    //               We only use the `test` and `p_value` fields.
    test_filter: Option<&str>,
    //           │
    //           └─ Some("shapiro") → only check shapiro's p-values.
    //              None → check every test name found in the data.
) -> Vec<PvalueUniformityResult> {
    // ── Step 1: group p-values by test name ──

    let mut by_test: std::collections::HashMap<&str, Vec<f64>> = std::collections::HashMap::new();
    //  │
    //  └─ HashMap<test_name, [p_values]>
    //     Key is &str (borrowed from the TestResult), not owned String.
    //     We don't need to outlive the input slice.

    for r in diagnostics {
        // Apply optional filter
        if let Some(filter) = test_filter {
            //  │       │          │
            //  │       │          └─ test_filter: Option<&str>
            //  │       │             Came from the function argument.
            //  │       │             Some("shapiro") = "only check this test"
            //  │       │             None = "check all tests"
            //  │       │
            //  │       └─ filter: &str — the inner value, unwrapped.
            //  │          Now holds "shapiro" (or whatever was passed).
            //  │
            //  └─ `if let Some(x) = ...`: pattern ① from the Option trio.
            //     "If there's a filter, bind it to `filter` and enter the block.
            //      If None, skip the block entirely — no filtering."

            if r.test != filter {
                continue;
            }
            //  │         │       │
            //  │         │       └─ continue: skip to the next iteration of the for loop.
            //  │         │          This r doesn't match the filter, so don't process it.
            //  │         │          Python: `if r.test != filter: continue`
            //  │         │          Identical keyword, identical meaning.
            //  │         │
            //  │         └─ filter: the &str we just unwrapped ("shapiro")
            //  │
            //  └─ r.test: the test name on this TestResult ("shapiro", "anderson", ...)
        }
        // If test_filter was None, this entire block is skipped.
        // Every TestResult passes through — no filtering.
        //
        // The two if's together read as:
        //   "If a filter was provided AND this result doesn't match it, skip."
        //
        // Python equivalent:
        //   if test_filter is not None and r.test != test_filter:
        //       continue

        by_test
            .entry(r.test.as_str())
            //     │
            //     └─ .as_str(): borrow the String as &str.
            //        The HashMap key is &str to avoid cloning every test name.
            .or_insert_with(Vec::new)
            /*
            says: "if vacant, call this function to create the default value and insert it.
            If occupied, do nothing."
            First iteration: r.test = "shapiro", r.p_value = 0.42
            .entry("shapiro")       → Vacant (not in map)
            .or_insert_with(Vec::new) → creates Vec::new(), inserts it → {"shapiro": []}
            .push(0.42)             → {"shapiro": [0.42]}
            The _with suffix means "lazy" — Vec::new is a function that only gets CALLED when needed (vacant case). Without _with, you'd write .or_insert(Vec::new()) which creates the Vec EVERY time, even when the key exists and the Vec gets thrown away. For a Vec that's wasteful.
            For a simple number like .or_insert(0), the waste is negligible so you skip the _with.
            Python equivalent: d.setdefault("shapiro", []).append(0.42)
             */
            .push(r.p_value);
    }

    // ── Step 2: for each test, probit-transform + Anderson-Darling ──

    let normal = Normal::new(0.0, 1.0).unwrap();
    //           │
    //           └─ Standard normal distribution.
    //              We need its INVERSE CDF (ppf / probit) below.

    by_test
        .into_iter()
        .filter_map(|(test_name, pvals)| {
            //  └─ .filter_map(): like .map() but can skip entries (returns Option).
            //     If we can't compute the AD test (too few valid p-values),
            //     we return None and the entry is dropped from the output.

            // ── Probit transform: p → Φ⁻¹(p) ──
            //
            // Filter out p-values at the boundaries (0.0 and 1.0)
            // because Φ⁻¹(0) = -∞ and Φ⁻¹(1) = +∞.
            // Also filter NaN/Inf from degenerate test results.
            //
            // DELIBERATELY NOT MERGED with `anderson_darling_probit_against_cdf`
            // (which CLAMPS to [1e-12, 1-1e-12] instead of filtering): this path
            // is the formula-exact port of the frozen Python (`flat_p_clean` +
            // scipy ppf), pinned by parity tests, and its drop-the-boundary
            // policy is part of that contract. The clamp helper serves the
            // group-GOF path and retains boundary evidence. Two contracts,
            // two policies — reviewed 2026-08-12, kept apart on purpose.

            let z_scores: Vec<f64> = pvals
                .iter()
                .filter(|&&p| p.is_finite() && p > 0.0 && p < 1.0)
                //              │
                //              └─ Three guards:
                //                 .is_finite() — reject NaN and ±Inf
                //                 p > 0.0 — reject exactly zero (probit = -∞)
                //                 p < 1.0 — reject exactly one (probit = +∞)
                .map(|&p| normal.inverse_cdf(p))
                //         │
                //         └─ .inverse_cdf(p) = Φ⁻¹(p) = the probit function.
                //            If p ~ U(0,1), then Φ⁻¹(p) ~ N(0,1).
                //
                //            Python: scipy.stats.norm.ppf(p) — same function.
                //            statrs calls it .inverse_cdf().
                //
                //            Examples:
                //              p = 0.5   → z = 0.0    (median)
                //              p = 0.975 → z = 1.96   (97.5th percentile)
                //              p = 0.025 → z = -1.96  (2.5th percentile)
                .collect();

            if z_scores.len() < 3 {
                // Need at least 3 points for Anderson-Darling.
                return None;
            }

            // ── Run Anderson-Darling on the z-scores ──
            //
            // If the z-scores are N(0,1), the original p-values were U(0,1),
            // meaning the normality test was behaving as expected under H₀.

            let ad = normality::anderson_darling(z_scores.iter().copied());
            //                                   │
            //                                   └─ .iter().copied(): &[f64] → iterator of f64.
            //                                      Same pattern as in normality_battery (§8).

            match ad {
                Ok(result) => Some(PvalueUniformityResult {
                    test: test_name.to_string(),
                    //              │
                    //              └─ .to_string(): &str → owned String.
                    //                 We're building an owned struct from borrowed keys.
                    ad_statistic: result.statistic,
                    p_value_of_pvalues: result.p_value,
                }),
                Err(_) => None,
                //        │
                //        └─ AD failed (degenerate data). Skip this test.
                //           Python: try/except → append NaN row.
                //           Rust: filter_map → drop the entry entirely.
                //           Cleaner: no NaN rows polluting the output.
            }
        })
        .collect()

    // Typical output for a well-specified model:
    //   [
    //     { test: "shapiro",  ad_statistic: 0.3, p_value_of_pvalues: 0.58 },
    //     { test: "anderson", ad_statistic: 0.4, p_value_of_pvalues: 0.34 },
    //   ]
    //   All p_value_of_pvalues > 0.05 → p-values are uniform → model is well-specified.
    //
    // For a mis-specified model:
    //   [
    //     { test: "shapiro",  ad_statistic: 2.1, p_value_of_pvalues: 0.003 },
    //   ]
    //   p_value_of_pvalues < 0.05 → p-values cluster (all too small or all too large)
    //   → model has a systematic problem across features.
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑪  EnbPIResult — conformal prediction output (ndarray edition)
//
// EnbPI (Ensemble Batch Prediction Intervals) is a distribution-free
// method for constructing prediction intervals. It doesn't assume
// normality — it uses bootstrap resampling of the model's own
// residuals to estimate quantiles.
//
// The algorithm:
//   1. Split data into train/test
//   2. For b = 1..B: resample training data, fit model, predict
//   3. Compute out-of-bag (OOB) residuals
//   4. Take the (1-α) quantile of OOB residuals as the interval width
//   5. Apply: [pred - width, pred + width] for test predictions
//
// Why this matters for Fern:
//   The Brenier map gives you a learned distribution. EnbPI gives you
//   COVERAGE GUARANTEES without trusting the distribution. If Fern's
//   Brenier map is well-calibrated, the EnbPI intervals should match
//   the Brenier-derived intervals. Disagreement = calibration problem.
//
// Current state:
//   The STRUCT lives in Rust (holds results, computes coverage/width).
//   The COMPUTATION stays in Python (needs the model for B refits).
//
// Future (Burn):
//   Rust runs EnbPI natively. B bootstrap reps in par_iter.
//
// Python twin: class EnbPIResult in fr_tr_exp.py
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ──────────────────────────────────────────────────────────────────────
// §18  EnbPIResult
// ──────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct EnbPIResult {
    pub test_pred: Array3<f64>,
    //             │
    //             └─ [N_test, H, D]: model predictions on test windows.
    //                N_test = number of test windows
    //                H = prediction horizon (= pred_len)
    //                D = number of features
    pub test_true: Array3<f64>,
    //             └─ [N_test, H, D]: ground truth. Paired with test_pred.
    pub lower: Array3<f64>,
    //         └─ [N_test, H, D]: lower bound of prediction interval.
    pub upper: Array3<f64>,
    //         └─ [N_test, H, D]: upper bound.
    pub oob_residuals: Array1<f64>,
    //                 │
    //                 └─ [N_oob]: out-of-bag residuals from bootstrap.
    //                    1D array. The (1-α) quantile → interval width.
    pub quantile_width: f64,
    pub alpha: f64,
    pub num_bootstrap: usize,
    pub block_size: usize,
    pub dataset: String,
}

/// numpy's default quantile ("linear" interpolation), reproduced exactly:
/// position = (n-1)·q; the value is x[floor] + frac·(x[floor+1] - x[floor])
/// over the SORTED values. Kept private: callers get it through the EnbPI
/// constructor, which is the only place this definition is part of a contract.
fn linear_interpolated_quantile(values: &[f64], q: f64) -> f64 {
    let mut sorted = values.to_vec();
    sorted.sort_by(f64::total_cmp);
    let position = (sorted.len() - 1) as f64 * q;
    let lower_index = position.floor() as usize;
    let fraction = position - lower_index as f64;
    if fraction == 0.0 || lower_index + 1 == sorted.len() {
        sorted[lower_index]
    } else {
        sorted[lower_index] + fraction * (sorted[lower_index + 1] - sorted[lower_index])
    }
}

impl EnbPIResult {
    /// The EnbPI aggregation, ported formula-exact from the frozen Python
    /// (`retired/fr_tr_exp/fr_tr_exp_with_enbpi_2026_07_01.py`,
    /// `_compute_oob_residuals` + Phase 4 of `run_enbpi`), for the
    /// registered-plan rebuild where the B members' forecasts arrive as
    /// committed artifacts instead of live models.
    ///
    /// Inputs, in the frozen code's own vocabulary:
    ///   member_train_preds  — B × [N_train, H, D], every member's point
    ///                         predictions on the SHARED original training
    ///                         windows (not its bootstrap resample).
    ///   train_true          — [N_train, H, D] targets for those windows.
    ///   oob_block_masks     — B × [K] with K = N_train / block_size, true =
    ///                         that block of windows is OUT of member b's
    ///                         resample, so b predicts it honestly.
    ///   member_test_preds   — B × [N_test, H, D]; the ensemble point
    ///                         forecast is their per-element mean over ALL B.
    ///
    /// The frozen recipe, kept exactly (#36):
    ///   window i belongs to block i / block_size; windows past the last
    ///   FULL block are dropped; a window no member holds out is skipped;
    ///   the residual is the scalar mean-absolute-error of the held-out
    ///   members' mean prediction; the band is the (1-α) linear-interpolated
    ///   quantile (numpy's default) of those residuals, added to and
    ///   subtracted from the ensemble forecast as one constant width.
    pub fn from_member_forecasts(
        member_train_preds: &[Array3<f64>],
        train_true: &Array3<f64>,
        oob_block_masks: &[Vec<bool>],
        block_size: usize,
        member_test_preds: &[Array3<f64>],
        test_true: Array3<f64>,
        alpha: f64,
        dataset: String,
    ) -> Result<EnbPIResult, String> {
        let member_count = member_train_preds.len();
        if member_count == 0 {
            return Err("EnbPI needs at least one member; got zero".to_string());
        }
        if oob_block_masks.len() != member_count || member_test_preds.len() != member_count {
            return Err(format!(
                "EnbPI member counts disagree: {member_count} train forecasts, {} masks, {} test forecasts",
                oob_block_masks.len(),
                member_test_preds.len()
            ));
        }
        if !(alpha > 0.0 && alpha < 1.0) {
            return Err(format!("alpha must be inside (0, 1); got {alpha}"));
        }
        if block_size == 0 {
            return Err("block_size must be positive".to_string());
        }
        let (train_window_count, horizon, channels) = {
            let shape = train_true.shape();
            (shape[0], shape[1], shape[2])
        };
        let full_block_count = train_window_count / block_size;
        for (member, mask) in oob_block_masks.iter().enumerate() {
            if mask.len() != full_block_count {
                return Err(format!(
                    "member {member}'s mask covers {} blocks, but {train_window_count} windows at block_size \
                     {block_size} make {full_block_count} full blocks",
                    mask.len()
                ));
            }
        }
        for (member, predictions) in member_train_preds.iter().enumerate() {
            if predictions.shape() != train_true.shape() {
                return Err(format!(
                    "member {member}'s train predictions have shape {:?}, targets {:?}",
                    predictions.shape(),
                    train_true.shape()
                ));
            }
        }
        for (member, predictions) in member_test_preds.iter().enumerate() {
            if predictions.shape() != test_true.shape() {
                return Err(format!(
                    "member {member}'s test predictions have shape {:?}, targets {:?}",
                    predictions.shape(),
                    test_true.shape()
                ));
            }
        }

        // ── Per-window out-of-bag residuals, in window order ──
        //    Python: for i in range(N): block_id = i // block_size; ...
        let mut oob_residuals: Vec<f64> = Vec::new();
        for window in 0..train_window_count {
            let block = window / block_size;
            if block >= full_block_count {
                continue; // tail windows past the last full block, dropped
            }
            let held_out: Vec<usize> = (0..member_count)
                .filter(|&member| oob_block_masks[member][block])
                .collect();
            if held_out.is_empty() {
                continue; // every member trained on this block: no honest view
            }
            let mut absolute_error_sum = 0.0;
            for step in 0..horizon {
                for channel in 0..channels {
                    let mut prediction_sum = 0.0;
                    for &member in &held_out {
                        prediction_sum += member_train_preds[member][[window, step, channel]];
                    }
                    let held_out_mean = prediction_sum / held_out.len() as f64;
                    absolute_error_sum += (held_out_mean - train_true[[window, step, channel]]).abs();
                }
            }
            //    Python: (oob_pred - targets[i]).abs().mean().item()
            oob_residuals.push(absolute_error_sum / (horizon * channels) as f64);
        }
        if oob_residuals.is_empty() {
            return Err(
                "no training window is out-of-bag for any member; EnbPI intervals are impossible \
                 (every block was inside every resample)"
                    .to_string(),
            );
        }

        //    Python: q = float(np.quantile(oob_residuals, 1 - spec.alpha))
        let quantile_width = linear_interpolated_quantile(&oob_residuals, 1.0 - alpha);

        // ── Ensemble point forecast: per-element mean over ALL members ──
        //    Python: torch.stack([...]).mean(dim=0)
        let mut test_pred = Array3::<f64>::zeros(test_true.raw_dim());
        for predictions in member_test_preds {
            test_pred += predictions;
        }
        test_pred /= member_count as f64;
        let lower = &test_pred - quantile_width;
        let upper = &test_pred + quantile_width;

        Ok(EnbPIResult {
            test_pred,
            test_true,
            lower,
            upper,
            oob_residuals: Array1::from(oob_residuals),
            quantile_width,
            alpha,
            num_bootstrap: member_count,
            block_size,
            dataset,
        })
    }

    /// Empirical coverage: fraction of test points inside [lower, upper].
    pub fn coverage(&self) -> f64 {
        let inside = self
            .test_true
            .iter()
            //                         │
            //                         └─ .iter(): flat iterator over ALL elements.
            //                            Array3 with shape [N, H, D] yields N*H*D floats.
            //                            ndarray iterates in row-major order by default.
            .zip(self.lower.iter())
            .zip(self.upper.iter())
            //
            // Triple zip: (truth, lower, upper) for every element.
            // ndarray .iter() yields elements in the same order for same-shape arrays,
            // so the zip pairs corresponding elements correctly.
            //
            // Compare to the Vec<Vec<Vec>> version which was three nested for-loops.
            .filter(|&((&truth, &lo), &hi)| truth >= lo && truth <= hi)
            //       │
            //       └─ Destructure: the triple-zip yields ((&f64, &f64), &f64).
            //          Ugly but mechanical. filter keeps only points inside the interval.
            .count();

        let total = self.test_true.len();
        //                         │
        //                         └─ .len(): total number of elements (N * H * D).
        //                            Python: self.test_true.size

        if total == 0 {
            return 0.0;
        }
        inside as f64 / total as f64

        // Python version was:
        //   inside = (self.test_true >= self.lower) & (self.test_true <= self.upper)
        //   return float(inside.mean())
        //
        // ndarray CAN do this too:
        //   let mask = Zip::from(&self.test_true).and(&self.lower).and(&self.upper)
        //       .map_collect(|&t, &lo, &hi| if t >= lo && t <= hi { 1.0 } else { 0.0 });
        //   mask.mean().unwrap()
        //
        // But the iter().zip().filter().count() version is more readable
        // for someone learning Rust. Same result either way.
    }

    /// Mean width of prediction intervals.
    pub fn mean_width(&self) -> f64 {
        let widths = &self.upper - &self.lower;
        //          │
        //          └─ &Array3 - &Array3 → Array3: element-wise subtraction.
        //             Returns a new array with (upper - lower) at every element.
        //
        //             Python: (self.upper - self.lower).mean()
        //             Rust: (&self.upper - &self.lower).mean().unwrap()
        //             Almost identical.

        widths.mean().unwrap_or(0.0)
        //    │
        //    └─ .mean(): average of all elements. Returns Option<f64>.
        //       None only if the array is empty. .unwrap_or(0.0) for safety.
    }

    /// Human-readable summary.
    pub fn summary(&self) -> String {
        format!(
            "EnbPI | {} | B={} α={}\n\
             \x20 Coverage: {:.3} (target: {:.3})\n\
             \x20 Mean width: {:.4}\n\
             \x20 Quantile (1-α): {:.4}\n\
             \x20 OOB residuals: {} windows",
            self.dataset,
            self.num_bootstrap,
            self.alpha,
            self.coverage(),
            1.0 - self.alpha,
            self.mean_width(),
            self.quantile_width,
            self.oob_residuals.len(),
        )
    }

    /// Check whether coverage meets target within tolerance.
    pub fn is_calibrated(&self, tolerance: f64) -> bool {
        let target = 1.0 - self.alpha;
        (self.coverage() - target).abs() < tolerance
    }
}

/// Build the EnbPI evidence from B members' stored predictions.
///
/// The aggregation stage of the rebuilt EnbPI plan
/// (.ai/pending_patches/enbpi_native_rebuild.md): the B bootstrap trainings
/// are ordinary registered runs; this function consumes their committed point
/// predictions plus the declared out-of-bag block masks, and performs the
/// retired Python's phases formula-exactly (retired/fr_tr_exp/
/// fr_tr_exp_with_enbpi_2026_07_01.py, `_compute_oob_residuals` and
/// `run_enbpi` Phases 3-4):
///
///   1. per-window OOB residual — mean prediction of the members whose
///      resample EXCLUDED the window's block, scalar MAE against the target.
///      Windows past the last complete block are dropped (`block_id >= K`)
///      and windows that every member trained on are skipped, both exactly
///      as in the reference;
///   2. interval half-width `q` = the (1-alpha) quantile of those residuals
///      using numpy's default linear interpolation;
///   3. constant band — ensemble mean prediction ± q on the test windows.
///
/// `oob_block_masks[b][k]` is true when member `b`'s resample left block `k`
/// out (the member never saw it, so its prediction there is honest).
pub fn aggregate_enbpi_from_member_predictions(
    train_member_preds: &ndarray::Array4<f64>, // [B, N, H, D]
    train_targets: &Array3<f64>,               // [N, H, D]
    oob_block_masks: &[Vec<bool>],             // [B][K], true = block held out
    block_size: usize,
    test_member_preds: &ndarray::Array4<f64>, // [B, N_test, H, D]
    test_true: &Array3<f64>,                  // [N_test, H, D]
    alpha: f64,
    dataset: String,
) -> Result<EnbPIResult, String> {
    let (members, windows, horizon, channels) = train_member_preds.dim();
    if members == 0 {
        return Err("EnbPI needs at least one member".into());
    }
    if block_size == 0 {
        return Err("block_size must be positive".into());
    }
    if !(alpha > 0.0 && alpha < 1.0) {
        return Err(format!("alpha must lie in (0,1), got {alpha}"));
    }
    if train_targets.dim() != (windows, horizon, channels) {
        return Err(format!(
            "train_targets shape {:?} does not match member predictions {:?}",
            train_targets.dim(),
            train_member_preds.dim()
        ));
    }
    let complete_blocks = windows / block_size;
    if oob_block_masks.len() != members || oob_block_masks.iter().any(|mask| mask.len() != complete_blocks) {
        return Err(format!(
            "oob_block_masks must be [{members}][{complete_blocks}] (members x complete blocks)"
        ));
    }
    let (test_members, test_windows, test_horizon, test_channels) = test_member_preds.dim();
    if test_members != members || (test_horizon, test_channels) != (horizon, channels) {
        return Err("test predictions disagree with train predictions on members or [H,D]".into());
    }
    if test_true.dim() != (test_windows, horizon, channels) {
        return Err("test_true shape does not match test predictions".into());
    }

    // Phase 2 of the reference: one scalar MAE residual per train window whose
    // block at least one member held out.
    let mut oob_residuals: Vec<f64> = Vec::new();
    for window in 0..windows {
        let block_id = window / block_size;
        if block_id >= complete_blocks {
            continue; // tail windows past the last complete block, as in the reference
        }
        let holdout_members: Vec<usize> = (0..members).filter(|&b| oob_block_masks[b][block_id]).collect();
        if holdout_members.is_empty() {
            continue; // every member trained on this block; no honest prediction exists
        }
        let mut absolute_error_sum = 0.0;
        for h in 0..horizon {
            for d in 0..channels {
                let oob_mean = holdout_members
                    .iter()
                    .map(|&b| train_member_preds[[b, window, h, d]])
                    .sum::<f64>()
                    / holdout_members.len() as f64;
                absolute_error_sum += (oob_mean - train_targets[[window, h, d]]).abs();
            }
        }
        oob_residuals.push(absolute_error_sum / (horizon * channels) as f64);
    }
    if oob_residuals.is_empty() {
        return Err("no window was left out by any member; residual pocket is empty".into());
    }

    // Phase 4: numpy's default linear-interpolation quantile of the pocket.
    let mut sorted = oob_residuals.clone();
    sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let position = (sorted.len() - 1) as f64 * (1.0 - alpha);
    let below = position.floor() as usize;
    let above = position.ceil() as usize;
    let quantile_width = sorted[below] + (position - below as f64) * (sorted[above] - sorted[below]);

    // Phase 3: ensemble mean over ALL members on the test windows, then the
    // constant band pred ± q.
    let mut test_pred = Array3::<f64>::zeros((test_windows, horizon, channels));
    for window in 0..test_windows {
        for h in 0..horizon {
            for d in 0..channels {
                test_pred[[window, h, d]] =
                    (0..members).map(|b| test_member_preds[[b, window, h, d]]).sum::<f64>() / members as f64;
            }
        }
    }
    let lower = test_pred.mapv(|v| v - quantile_width);
    let upper = test_pred.mapv(|v| v + quantile_width);

    Ok(EnbPIResult {
        test_pred,
        test_true: test_true.clone(),
        lower,
        upper,
        oob_residuals: Array1::from(oob_residuals),
        quantile_width,
        alpha,
        num_bootstrap: members,
        block_size,
        dataset,
    })
}

// §19  Loading EnbPIResult from disk — deferred
//
// Sketch only. The active artifact rule is: store raw tensors only when an
// explicit plan knob asks for forensic output, and prefer safetensors over
// ad-hoc .npy sidecars.
//
// Cargo.toml:
//   safetensors = "..."
//
// impl EnbPIResult {
//     pub fn load(dir: &Path) -> Result<Self, Box<dyn std::error::Error>> {
//         let config: EnbPIConfig = serde_json::from_str(
//             &std::fs::read_to_string(dir.join("config.json"))?
//         )?;
//         let tensors = load_safetensors(dir.join("raw_tensors.safetensors"))?;
//         let test_pred: Array3<f64> = tensors.array3("test_pred")?;
//         let test_true: Array3<f64> = tensors.array3("test_true")?;
//         let lower: Array3<f64>     = tensors.array3("lower")?;
//         let upper: Array3<f64>     = tensors.array3("upper")?;
//         let oob: Array1<f64>       = tensors.array1("oob_residuals")?;
//         Ok(Self {
//             test_pred, test_true, lower, upper,
//             oob_residuals: oob,
//             quantile_width: config.quantile_width,
//             alpha: config.alpha,
//             num_bootstrap: config.B,
//             block_size: config.block_size,
//             dataset: config.dataset,
//         })
//     }
// }

// Calibration diagnostics now use scipy/statsmodels in
// python/fern_analysis/calibration_diagnostic.py. The retired Rust block is
// preserved under retired/fern_post for migration evidence.
// `ks_test_uniform` (vs U(0,1)) used to repeat the same D-max loop and the
// same asymptotic series byte for byte; both now feed this function their CDF.
pub(crate) fn ks_test_against_cdf<F>(sample: &[f64], cdf: F) -> (f64, f64)
where
    F: Fn(f64) -> f64,
{
    let n = sample.len() as f64;
    let mut sorted = sample.to_vec();
    sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    //                      │
    //                      └─ Floats in Rust don't implement full Ord (because
    //                         of NaN). partial_cmp returns Option<Ordering>;
    //                         NaNs sort as Equal here rather than panicking.

    // KS statistic: max|F_empirical(x_i) - F_theoretical(x_i)|
    let mut d_max: f64 = 0.0;
    for (i, &x) in sorted.iter().enumerate() {
        let f_emp = (i + 1) as f64 / n; // empirical CDF at x_i
        let f_theory = cdf(x).clamp(0.0, 1.0);
        //             │
        //             └─ cdf(x): the theoretical cumulative distribution
        //                P(X ≤ x). Python: scipy.stats.<dist>.cdf(x).
        let d_plus = (f_emp - f_theory).abs();
        let d_minus = (f_theory - (i as f64 / n)).abs();
        d_max = d_max.max(d_plus).max(d_minus);
    }

    // p-value approximation (Kolmogorov-Smirnov distribution)
    // Asymptotic formula: p ≈ 2 * Σ (-1)^(k-1) * exp(-2k²λ²), with the
    // Numerical-Recipes finite-n correction λ = (√n + 0.12 + 0.11/√n)·D.
    let sqrt_n = n.sqrt();
    let lambda = (sqrt_n + 0.12 + 0.11 / sqrt_n) * d_max;
    let mut p = 0.0;
    for k in 1..=100 {
        let sign = if k % 2 == 1 { 1.0 } else { -1.0 };
        p += sign * (-2.0 * (k as f64).powi(2) * lambda.powi(2)).exp();
    }
    (d_max, (2.0 * p).clamp(0.0, 1.0))
    //                │
    //                └─ .clamp(0.0, 1.0): numerical error can push the series
    //                   slightly outside [0, 1].
}
/*
Calibrated (flat): If F_θ matches reality perfectly,
    then u = F_θ(y) maps each true y through the correct CDF.
    By definition of a CDF, this produces U(0,1). Every bin gets equal mass. Flat.

    Underdispersed / too narrow (U-shaped): The model thinks it's more certain than it is — predicted variance is too small,
    so the predicted CDF is too steep (concentrated around the predicted mean). When the true y falls in the tails (which happens more often than the model expects),
    F_θ(y) gets pushed toward 0 or 1. Picture a steep sigmoid: values slightly below the mean map to near 0, values slightly above map to near 1.
    The middle range of u barely gets used. Mass piles up at both ends → U-shape.
    Concretely: if σ_model = 0.5 but σ_true = 1.0, then a true y that's 1.5σ_true away from μ is "normal" in reality but the model thinks it's 3σ_model away — so F_θ(y) ≈ 0.999 or 0.001.
    Lots of u values near the edges.

    Overdispersed / too wide (hump-shaped): The model thinks it's less certain than it is — predicted variance is too large,
    so the predicted CDF is too gradual (stretched out). Most true y values fall in the "easy" middle region of this gentle CDF, so F_θ(y) clusters around 0.5.
    The extremes of u (near 0 or 1) are underrepresented because the model's tails extend far beyond where actual data lives. Mass concentrates in the center → hump.
    Concretely: if σ_model = 2.0 but σ_true = 0.5, then most true y values are within ±1σ_true of μ, which in the model's frame is only ±0.25σ_model.
    So F_θ(y) maps them all to the narrow band around 0.5 → everything piles in the middle.
    The mnemonic: the PIT histogram mirrors the model's error. Too confident → surprises at the edges → U. Too cautious → everything is boring middle → hump.
*/
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Tests
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Numerical parity tests — Rust diagnostics vs authoritative Python
//
// Goal: tell which Rust diagnostics MATCH Python (safe to bridge) and which
// DIVERGE (keep Python / flag for fix). Inputs are FROZEN f64 literals,
// bit-identical to scratchpad/reference.py (Python repr() round-trips to the
// same IEEE-754 bits that Rust parses). References produced by:
//   uv run --no-project --with scipy --with statsmodels --with pingouin \
//       python reference.py
//
// Honest-findings rule: tolerances are NOT widened to force a pass. Where a
// function diverges, the test asserts the ACTUAL Rust value and the comment
// records the Python value + divergence magnitude.
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
/// The crate's one Benjamini-Hochberg implementation: adjusted p-values via
/// the reverse running-min step-up. Rejection at level q is `adjusted <= q`,
/// which provably equals the textbook max-k scan (reject ranks 1..=k where
/// k = max{ i : p_(i) <= (i/m) q }) — both formulations use `<=`.
pub(crate) fn benjamini_hochberg_adjusted(pvals: &[f64]) -> Vec<f64> {
    let m = pvals.len() as f64;
    let mut indexed: Vec<(usize, f64)> = pvals.iter().copied().enumerate().collect();
    indexed.sort_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(std::cmp::Ordering::Equal));
    let mut adjusted = vec![0.0f64; pvals.len()];
    let mut running_min = 1.0f64;
    for (rank_minus_1, &(orig_idx, p)) in indexed.iter().enumerate().rev() {
        let rank = (rank_minus_1 + 1) as f64;
        running_min = running_min.min((p * m / rank).min(1.0));
        adjusted[orig_idx] = running_min;
    }
    adjusted
}

#[cfg(test)]
mod parity {
    use super::*;
    use normality::multivariate::{HenzeZirklerMethod, MardiaMethod, henze_zirkler, mardia};

    const SIGMA: f64 = 2.0;

    fn normal_sample() -> Vec<f64> {
        vec![
            2.257761446724792,
            1.2017255395205004,
            -2.1895896426071935,
            -1.6846739800850823,
            0.6934833006387556,
            2.021188484039968,
            2.2929184935569675,
            0.4731792502913461,
            0.6838221115286751,
            1.5716131757963752,
            0.2941609644218226,
            -2.7389086036114807,
            2.394744961616011,
            -3.268877864300475,
            0.17836276146509117,
            4.443849267881553,
            2.3923179144974216,
            1.6750395478560616,
            2.1875218471204256,
            2.70691183441918,
            1.8790687132233963,
            1.8202029632496546,
            2.0478041442260353,
            2.0474503187130284,
            -0.7977230935229919,
            2.9715096160467094,
            3.169872486694247,
            -1.7465663063675974,
            3.3471703978956358,
            -2.8550472252652552,
        ]
    }

    fn nonnormal_sample() -> Vec<f64> {
        vec![
            2.4874682068773284,
            1.1397737190612034,
            1.5366832477379861,
            2.040182394887819,
            1.702941220026021,
            1.3690893786643743,
            0.0767103773987039,
            0.15813090251944534,
            0.12225689984384627,
            0.12202225910152832,
            0.2315980333471781,
            2.0630301690254216,
            1.5255634789103731,
            0.4218319297481088,
            2.0009665133520578,
            0.280805567945288,
            0.18496638828434708,
            0.20459722314391657,
            0.2514121682195889,
            0.3123677503345253,
            0.42548204267707834,
            1.2017162733092555,
            0.7919117177842843,
            0.020971520778208033,
            1.0814412362403358,
            0.30406823461160565,
            0.39980826131738495,
            0.2844705323157501,
            0.5649029242306317,
            3.0265787633259187,
        ]
    }

    fn mv_matrix() -> Vec<Vec<f64>> {
        vec![
            vec![2.0246333335218556, 0.9782826325144267, 1.0231832738883606],
            vec![0.1355140636251958, 0.5148502777011478, 0.45268567687245553],
            vec![-0.5090436870276219, 0.3578029578798765, -0.5537238098735204],
            vec![0.9333385804796368, 0.21895471048727377, 0.5458173562615594],
            vec![-2.327745344391805, -2.961411270331929, -1.4638342863007616],
            vec![-1.8057607959304596, -2.7348650235703946, -0.733147294967141],
            vec![1.8403970341951457, 2.4410807976571136, 1.696564428307203],
            vec![-0.40579744746010205, -0.18824555683757035, -1.012833606949141],
            vec![-1.012357816435811, -0.10538084952434339, 1.0137259725634564],
            vec![-0.3706641405431296, 0.016160686038097755, -0.27081340280605043],
            vec![-0.02460676569116971, -0.3255977977254765, 0.9517265980094088],
            vec![-0.10517468490817714, 0.37032327394431763, 0.36289437966418187],
            vec![-1.5771109034786985, -0.39023719896112913, -2.017884878423795],
            vec![-0.9054060258991863, 1.009748266442672, 0.14584937513925797],
            vec![-0.8297415033123059, -0.9072582518675005, 1.5496760859967575],
            vec![0.15585768635741512, 0.3903800390457551, 1.2693161173987308],
            vec![1.3792638514648157, -0.4213556729711356, -0.5544804692678931],
            vec![1.8988981604967796, 2.0525968786466793, 0.1800932912925998],
            vec![0.26439796970446067, 0.16937383133990241, 0.7704745292315074],
            vec![-0.08614058812196243, 0.2338291621739098, 0.5016595858773644],
            vec![-0.3214648769172585, -1.1400023034609794, -0.3226446173067084],
            vec![-0.7948220447959354, -0.035312105291780464, -0.10252341973587173],
            vec![-1.564635765860734, -0.5268699095319601, -0.7054661215096897],
            vec![0.43273410451484123, -0.17905758982073888, 1.03167593232499],
            vec![0.23326820038775628, -0.6372421225539889, -1.7255628782064456],
            vec![-0.7147889205048701, -0.3682092475737666, -0.7297246065085286],
            vec![1.9368924015143767, 0.4816651453205906, -0.5282339007305794],
            vec![-1.0135811695572896, -0.02350154916524811, 0.8323365805877818],
            vec![-0.10997938641565579, 0.3809092693921412, -1.0872872765890413],
            vec![0.2409776749902074, 0.3292738723018542, 0.7971195514557254],
            vec![1.3045102182442598, 0.5344816062163275, 0.21492922705490225],
            vec![0.653411794000769, 0.6453738062565193, 1.8649071335494023],
            vec![-0.029563029581940955, 1.4928877314986884, -0.15181111757371188],
            vec![1.1902840332193434, 0.7842172946902006, 0.5732170121773128],
            vec![-0.9517158477779265, -1.3445303192108624, 0.8339737674920092],
            vec![0.5494045874867917, 0.21229034554971202, 0.7964939747460614],
            vec![-1.345356888760448, 0.12282113712635162, 0.563631223308781],
            vec![-0.5998447094859404, 0.16184418302204123, -1.1825606833642612],
            vec![-1.537046347852312, -1.8533250882406413, -1.3063040423911163],
            vec![-1.5135066712253307, -2.914407705360335, -1.5316005774312285],
        ]
    }

    // ── 0. The committed copies of the three inputs ──────────────────────────
    // PARITY.md's record is only re-runnable by someone else if the inputs it
    // was measured on are in the repository. They now are, in
    // `fern_post/tests/fixtures/parity/`, and this test is what makes those
    // files trustworthy: it compares them value by value against the literals
    // above — the party to the comparison that cannot be edited into agreement
    // by whoever regenerates a fixture. The Python side reads the same three
    // files (`tests/test_parity_fixtures.py`), so both halves of the record now
    // run from one set of bytes rather than from a session scratchpad.
    #[test]
    fn the_committed_fixture_files_hold_exactly_these_three_inputs() {
        fn read_rows(name: &str) -> Vec<Vec<f64>> {
            let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("tests/fixtures/parity")
                .join(name);
            let text = std::fs::read_to_string(&path).unwrap_or_else(|error| panic!("{}: {error}", path.display()));
            text.lines()
                .filter(|line| !line.trim().is_empty())
                .map(|line| {
                    line.split_whitespace()
                        .map(|field| {
                            field
                                .parse::<f64>()
                                .unwrap_or_else(|error| panic!("{} holds a non-numeric field {field:?}: {error}", name))
                        })
                        .collect()
                })
                .collect()
        }
        fn read_column(name: &str) -> Vec<f64> {
            read_rows(name)
                .into_iter()
                .map(|row| {
                    assert_eq!(row.len(), 1, "{name} must hold one value per line");
                    row[0]
                })
                .collect()
        }

        // Bit-exact: the files hold the shortest round-trip decimal of each
        // value, and Rust's f64 parse is correctly rounded, so a single changed
        // bit anywhere fails here rather than shifting a p-value quietly.
        assert_eq!(read_column("normal_sample_n30.txt"), normal_sample());
        assert_eq!(read_column("skewed_sample_n30.txt"), nonnormal_sample());
        assert_eq!(read_rows("correlated_matrix_40x3.txt"), mv_matrix());

        // The shapes PARITY.md names, so a truncated file is a failure rather
        // than a shorter agreement.
        assert_eq!(normal_sample().len(), 30);
        assert_eq!(nonnormal_sample().len(), 30);
        assert_eq!(mv_matrix().len(), 40);
        assert!(mv_matrix().iter().all(|row| row.len() == 3));
    }

    // Assert |actual-expected| <= tol; the panic message carries the measured |Δ|
    // so a tightened/loosened tolerance is always visible in the failure.
    fn within(actual: f64, expected: f64, tol: f64, what: &str) {
        let d = (actual - expected).abs();
        assert!(
            d <= tol,
            "{what}: |Δ|={d:e} > tol={tol:e}  (rust={actual}, py={expected})"
        );
    }

    // Assert two values are genuinely FAR apart — a divergence finding must be a real
    // gap, not floating-point noise. Used to pin down the case-mismatch / cov-flag bugs.
    fn diverges(actual: f64, reference: f64, min_gap: f64, what: &str) {
        let d = (actual - reference).abs();
        assert!(d >= min_gap, "{what}: expected divergence but |Δ|={d:e} < {min_gap:e}");
    }

    // ── 1. KS exact ──────────────────────────────────────────────────────────────
    // Reference: scipy.stats.kstest(normal, 'norm', args=(0, 2.0))  (two-sided)
    // VERDICT: statistic MATCH; p-value approximate-but-decision-safe.
    #[test]
    fn ks_exact_vs_scipy() {
        let (stat, pval) = ks_test_exact(&normal_sample(), SIGMA);
        // statistic: |Δ| = 2.1e-11 (statrs vs scipy normal-CDF rounding only)
        within(stat, 0.3506762634301068, 1e-9, "KS statistic");
        // p-value: Rust uses the asymptotic Kolmogorov series (Numerical-Recipes
        // probks with the +0.12+0.11/√n correction); scipy uses the EXACT kstwo
        // distribution. They agree to |Δ| = 1.45e-5 — same accept/reject, but the
        // Rust p-value is an asymptotic approximation, not exact.
        within(pval, 0.0008378670833592006, 2e-5, "KS p-value (asymptotic vs exact)");
    }

    // ── 2. Cramér–von Mises, KNOWN sigma ─────────────────────────────────────────
    // Reference (correct for a fully-specified CDF): scipy.stats.cramervonmises(
    //     normal, 'norm', args=(0, 2.0))  → case-0 asymptotic p-value.
    // VERDICT: statistic MATCH; p-value DIVERGES catastrophically (case mismatch).
    #[test]
    fn cvm_known_sigma_stat_matches_but_pvalue_is_wrong_case() {
        let (stat, pval) = cramer_von_mises(&normal_sample(), Some(SIGMA));
        // W² statistic: |Δ| = 6.8e-11 vs scipy → MATCH.
        within(stat, 1.1287057147848285, 1e-9, "CvM W² statistic");
        // p-value TRAP: sigma is KNOWN → the reference CDF is FULLY SPECIFIED (case 0).
        // But cramer_von_mises applies cvm_p_value_normal_case3 — the Stephens (1976)
        // ESTIMATED-parameter (case 3) correction `W²·(1+0.5/n)` + case-3 critical
        // table. Here w2*=1.148 (>1.1) hits the table's lower clamp → 7.37e-10,
        // whereas the correct case-0 p-value is 1.06e-3 (~6 orders of magnitude off).
        within(pval, 7.37e-10, 1e-15, "CvM p-value = case-3 clamp (the bug)");
        diverges(pval, 0.0010603293185371676, 1e-4, "CvM p-value vs correct case-0");
    }

    // ── 3. Ljung–Box ─────────────────────────────────────────────────────────────
    // Reference: statsmodels.stats.diagnostic.acorr_ljungbox(normal, lags=[10])
    // VERDICT: MATCH to machine precision (stat & p-value).
    #[test]
    fn ljung_box_vs_statsmodels() {
        let (stat, pval) = ljung_box(&normal_sample(), 10);
        within(stat, 2.160988480599851, 1e-9, "Ljung-Box Q"); // |Δ| < 1e-13
        within(pval, 0.9949507080308608, 1e-9, "Ljung-Box p"); // |Δ| < 1e-13
    }

    // ── 4. Shapiro–Wilk ──────────────────────────────────────────────────────────
    // Reference: scipy.stats.shapiro.  Both implement Royston's AS R94 (1995).
    // VERDICT: MATCH to machine precision — the suspected algorithm-variant
    //          divergence does NOT occur (crate weights are f32 but W & p agree
    //          with scipy to ~1e-16 on n=30).
    #[test]
    fn shapiro_vs_scipy() {
        let sw = normality::shapiro_wilk(normal_sample().iter().copied()).unwrap();
        within(sw.statistic, 0.9139974896259737, 1e-9, "Shapiro W (normal)");
        within(sw.p_value, 0.01879408791117338, 1e-9, "Shapiro p (normal)");
        let sw = normality::shapiro_wilk(nonnormal_sample().iter().copied()).unwrap();
        within(sw.statistic, 0.8551382471426371, 1e-9, "Shapiro W (nonnormal)");
        within(sw.p_value, 0.0007958307913039934, 1e-9, "Shapiro p (nonnormal)");
    }

    // ── 5. Anderson–Darling ──────────────────────────────────────────────────────
    // Reference: statsmodels.stats.diagnostic.normal_ad (A², p).  Crate standardizes
    // with ddof=1 (== scipy.stats.anderson statistic) and uses the D'Agostino–Stephens
    // adjusted-A² p-value (== statsmodels normal_ad).
    // VERDICT: MATCH (statistic ~1e-11, p-value ~1e-12).
    #[test]
    fn anderson_darling_vs_statsmodels() {
        let ad = normality::anderson_darling(normal_sample().iter().copied()).unwrap();
        within(ad.statistic, 1.115995009862523, 1e-9, "AD A² (normal)"); // |Δ|=1.8e-11
        within(ad.p_value, 0.00536281071294053, 1e-9, "AD p (normal)"); // |Δ|=5.7e-13
        let ad = normality::anderson_darling(nonnormal_sample().iter().copied()).unwrap();
        within(ad.statistic, 1.6885184938914115, 1e-9, "AD A² (nonnormal)"); // |Δ|=6.3e-12
        within(ad.p_value, 0.00019254933944376916, 1e-9, "AD p (nonnormal)"); // |Δ|=7.1e-15
    }

    // ── 6. Jarque–Bera ───────────────────────────────────────────────────────────
    // Reference: scipy.stats.jarque_bera.  Identical moment formula + χ²(2) sf.
    // VERDICT: MATCH to machine precision.
    #[test]
    fn jarque_bera_vs_scipy() {
        let jb = normality::jarque_bera(normal_sample().iter().copied()).unwrap();
        within(jb.statistic, 2.7587784830273394, 1e-9, "JB (normal)");
        within(jb.p_value, 0.25173225372823244, 1e-9, "JB p (normal)");
        let jb = normality::jarque_bera(nonnormal_sample().iter().copied()).unwrap();
        within(jb.statistic, 4.431983550162723, 1e-9, "JB (nonnormal)");
        within(jb.p_value, 0.10904531217791719, 1e-9, "JB p (nonnormal)");
    }

    // ── 7. Lilliefors ────────────────────────────────────────────────────────────
    // Reference: statsmodels.stats.diagnostic.lilliefors(x, 'norm', pvalmethod='table')
    // VERDICT: statistic MATCH (both ddof=1 KS-with-estimated-params, |Δ|~1e-16);
    //          p-value DIVERGES — crate ports R nortest::lillie.test's analytic
    //          approximation, statsmodels uses Dallal–Wilkinson + a table clamped
    //          at a 0.001 floor. The approximations are a different family.
    #[test]
    fn lilliefors_stat_matches_but_pvalue_diverges() {
        // normal: statistic matches; p-value 0.01361 (Rust) vs 0.01438 (statsmodels), |Δ|=7.7e-4
        let lf = normality::lilliefors(normal_sample().iter().copied()).unwrap();
        within(lf.statistic, 0.180735172109463, 1e-9, "Lilliefors D (normal)");
        within(lf.p_value, 0.013609813711244537, 1e-9, "Lilliefors p (Rust, normal)");
        diverges(
            lf.p_value,
            0.014380606194687338,
            5e-4,
            "Lilliefors p vs statsmodels (normal)",
        );

        // nonnormal: statistic matches; Rust extrapolates to 1.23e-4 while statsmodels
        // clamps small p at its 0.001 table floor → |Δ|=8.8e-4.
        let lf = normality::lilliefors(nonnormal_sample().iter().copied()).unwrap();
        within(lf.statistic, 0.23987385720093968, 1e-9, "Lilliefors D (nonnormal)");
        within(
            lf.p_value,
            0.00012334712594498436,
            1e-9,
            "Lilliefors p (Rust, nonnormal)",
        );
        diverges(
            lf.p_value,
            0.001,
            5e-4,
            "Lilliefors p vs statsmodels 0.001 clamp (nonnormal)",
        );
    }

    // ── 8. Henze–Zirkler ─────────────────────────────────────────────────────────
    // Reference: pingouin.multivariate_normality(MV) — uses POPULATION covariance
    // (np.cov(..., bias=True), 1/n) and the same log-normal p-value.
    // VERDICT (crate algorithm): MATCH to machine precision when fed population cov.
    #[test]
    fn henze_zirkler_crate_matches_pingouin_population_cov() {
        let hz = henze_zirkler(mv_matrix(), true, HenzeZirklerMethod::LogNormal).unwrap();
        within(hz.statistic, 0.9197665658774501, 1e-9, "HZ statistic (pop cov)"); // |Δ|=4.4e-15
        within(hz.p_value, 0.04289015225238767, 1e-9, "HZ p-value (pop cov)"); // |Δ|=1.8e-12
    }

    // VERDICT (as bridged by joint_normality): MATCHES pingouin after the 2026-06-28 cov-flag
    // fix. joint_normality now passes the crate `true` flag = POPULATION covariance (1/n) — the
    // convention pingouin uses — so the bridged HZ matches pingouin to ~1e-9. (Before the fix it
    // passed `false` = sample cov 1/(n-1) and diverged ~1.3e-2. The `false` below is the unrelated
    // `use_bootstrap` arg, not the cov flag.)
    #[test]
    fn joint_normality_henze_zirkler_matches_pingouin() {
        let joint = joint_normality(mv_matrix(), 0.05, false); // false = no bootstrap (LogNormal)
        let hz = joint.iter().find(|r| r.test.as_str() == "henze_zirkler").unwrap();
        within(hz.statistic, 0.9197665658774501, 1e-9, "HZ statistic (joint, pop cov)");
        within(hz.p_value, 0.04289015225238767, 1e-9, "HZ p-value (joint, pop cov)");
    }

    // ── 9. Mardia skewness/kurtosis ──────────────────────────────────────────────
    // No scipy/statsmodels/pingouin Mardia test exists. Reference = a faithful numpy
    // reimplementation of the crate's exact formula (scratchpad/reference.py), computed with
    // both covariance conventions. VERDICT: the crate's Mardia math is internally correct
    // (reproduces the numpy reimpl to machine precision under the matching cov flag). After the
    // 2026-06-28 fix joint_normality uses `true` = POPULATION covariance (1/n, textbook MLE),
    // so its Mardia matches the direct mardia(true) call. (Before: `false` = sample cov →
    // skew 8.44 / kurt −1.27.)
    #[test]
    fn mardia_matches_numpy_reimplementation() {
        let joint = joint_normality(mv_matrix(), 0.05, false); // false = no bootstrap
        let skew = joint.iter().find(|r| r.test.as_str() == "mardia_skewness").unwrap();
        let kurt = joint.iter().find(|r| r.test.as_str() == "mardia_kurtosis").unwrap();
        // population covariance (1/n) — joint_normality now matches the direct mardia(true) below.
        within(skew.statistic, 9.104549354920701, 1e-7, "Mardia skew stat (pop cov)");
        within(skew.p_value, 0.5222110874251588, 1e-7, "Mardia skew p (pop cov)");
        within(kurt.statistic, -0.8900716164672507, 1e-7, "Mardia kurt stat (pop cov)");
        within(kurt.p_value, 0.3734274324937742, 1e-7, "Mardia kurt p (pop cov)");

        // Direct mardia(true) agrees — confirms joint_normality is on population cov.
        let m = mardia(mv_matrix(), true, MardiaMethod::Asymptotic).unwrap();
        within(
            m.skewness.statistic,
            9.104549354920701,
            1e-7,
            "Mardia skew stat (direct)",
        );
        within(
            m.kurtosis.statistic,
            -0.8900716164672507,
            1e-7,
            "Mardia kurt stat (direct)",
        );
    }
    // ── EnbPI aggregation vs the frozen Python reference ─────────────────────────
    // Expected numbers computed by running the retired implementation's exact
    // formulas (retired/fr_tr_exp/fr_tr_exp_with_enbpi_2026_07_01.py,
    // `_compute_oob_residuals` + `run_enbpi` Phases 3-4) in numpy on this
    // fixture: residuals [3.9, 3.5, 5.6, 5.200000000000001], q10-of-pocket
    // 5.48, ensemble [0.25, 0.5, 0.75, 1.0], coverage 1.0, mean width 10.96.
    #[test]
    fn enbpi_aggregation_matches_frozen_python_reference() {
        let (members, windows, horizon, channels) = (3usize, 5usize, 2usize, 1usize);
        let train_preds = ndarray::Array4::from_shape_fn((members, windows, horizon, channels), |(b, i, h, d)| {
            (((b * windows + i) * horizon + h) * channels + d) as f64 * 0.5
        });
        let train_targets = Array3::from_shape_fn((windows, horizon, channels), |(i, h, d)| {
            ((i * horizon + h) * channels + d) as f64 * 0.7 + 1.0
        });
        let masks = vec![vec![true, false], vec![false, true], vec![true, true]];
        let test_preds = ndarray::Array4::from_shape_fn((members, 2, horizon, channels), |(b, w, h, d)| {
            ((((b * 2 + w) * horizon + h) * channels + d) as f64 - 3.0) * 0.25
        });
        let test_true = Array3::from_elem((2, horizon, channels), 0.4);

        let result = aggregate_enbpi_from_member_predictions(
            &train_preds,
            &train_targets,
            &masks,
            2,
            &test_preds,
            &test_true,
            0.1,
            "fixture".to_string(),
        )
        .unwrap();

        let expected_residuals = [3.9, 3.5, 5.6, 5.200000000000001];
        assert_eq!(result.oob_residuals.len(), expected_residuals.len());
        for (got, want) in result.oob_residuals.iter().zip(expected_residuals) {
            within(*got, want, 1e-12, "OOB residual");
        }
        within(result.quantile_width, 5.48, 1e-12, "quantile width");
        let expected_ensemble = [0.25, 0.5, 0.75, 1.0];
        for (got, want) in result.test_pred.iter().zip(expected_ensemble) {
            within(*got, want, 1e-12, "ensemble prediction");
        }
        within(result.coverage(), 1.0, 0.0, "coverage");
        within(result.mean_width(), 10.96, 1e-12, "mean width");
        assert_eq!(result.num_bootstrap, 3);
    }

    // Failure path: masks that hold nothing out leave the residual pocket
    // empty, and the aggregation must refuse rather than produce a NaN band.
    #[test]
    fn enbpi_aggregation_refuses_an_empty_residual_pocket() {
        let train_preds = ndarray::Array4::zeros((2, 4, 1, 1));
        let train_targets = Array3::zeros((4, 1, 1));
        let masks = vec![vec![false, false], vec![false, false]];
        let test_preds = ndarray::Array4::zeros((2, 1, 1, 1));
        let test_true = Array3::zeros((1, 1, 1));
        let error = aggregate_enbpi_from_member_predictions(
            &train_preds,
            &train_targets,
            &masks,
            2,
            &test_preds,
            &test_true,
            0.1,
            "fixture".to_string(),
        )
        .unwrap_err();
        assert!(error.contains("residual pocket is empty"), "{error}");
    }
    // ── BH consolidation: the adjusted<=q mask must equal the max-k scan ──
    #[test]
    fn benjamini_hochberg_mask_equals_the_max_k_scan_including_the_boundary() {
        // 0.025 == (1/4)*0.1 exactly: the canonical rule REJECTS at equality.
        let pvals = [0.025, 0.03, 0.5, 0.9];
        let q = 0.1;
        let adjusted = benjamini_hochberg_adjusted(&pvals);
        let mask: Vec<bool> = adjusted.iter().map(|&a| a <= q).collect();
        // Hand-computed max-k scan with <=: rank1 0.025<=0.025 ok,
        // rank2 0.03<=0.05 ok, rank3 0.5>0.075, rank4 0.9>0.1 -> k=2.
        assert_eq!(mask, vec![true, true, false, false]);
        // A strict-< rejection would drop the boundary case:
        assert!(adjusted[0] <= q && !(adjusted[0] < q) || adjusted[0] < q);
    }
}
