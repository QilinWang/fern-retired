# fern_run diagnostics — parity with Python (scipy / statsmodels / pingouin)

Status of `src/diagnostics.rs`'s statistical functions versus authoritative Python
references. This is the source of truth for **which diagnostics the Python→Rust bridge may
trust**. Parity tests live in `src/diagnostics.rs` → `#[cfg(test)] mod parity`.

Verified 2026-06-28 on fixed inputs — a 30-value N(0,2) sample, a 30-value skewed
(exponential) sample, a 40×3 correlated-normal matrix, σ=2.0 — encoded bit-identically
(`repr()` round-trip) in both the Python references and the Rust tests.

### The inputs are in the repository (2026-08-22)

Those three inputs used to live only in a session scratchpad, so this record could be
read but not re-run by anyone else. They are now committed at
[`tests/fixtures/parity/`](tests/fixtures/parity/) — `normal_sample_n30.txt`,
`skewed_sample_n30.txt`, `correlated_matrix_40x3.txt` — recovered verbatim from the
literals in `mod parity` rather than re-derived on a new seed, and both sides now read
them:

- `mod parity::the_committed_fixture_files_hold_exactly_these_three_inputs` compares the
  files value by value against those literals —
  `fern-go lane cargo -- nextest run -p fern_post -E 'test(parity)'`
- `tests/test_parity_fixtures.py` re-runs every scipy / statsmodels / pingouin reference
  in the table below from the same files —
  `/home/user/.venv/bin/python -m pytest tests/test_parity_fixtures.py -q`

The original generator and reference script (`gen_inputs.py`, `reference.py`) were in that
scratchpad and were not recovered; the values were, which is what the record depends on.

**Two upstream movements found when re-running under scipy 1.18.0 / statsmodels 0.14.6.**
Neither changes a verdict, and both are now pinned by the Python test so a larger drift
fails rather than passing silently. Shapiro–Wilk on the normal sample now returns
W = 0.9139974894454844 against the 2026-06-28 record's 0.9139974896259737 — the same
number to 1.8e-10 rather than to ~1e-16, because scipy's implementation moved, not Fern's;
that is still three orders inside the 1e-9 tolerance every parity test uses. And
`scipy.stats.kstest(x, "norm", args=(0, 2.0))` now raises
`TypeError: ndtr() takes from 1 to 2 positional arguments but 3 were given`, so the Python
test passes the frozen `stats.norm(0, 2.0).cdf` instead — the same fully-specified CDF,
reproducing the recorded numbers exactly.

## Summary

| function | Python reference | statistic | p-value | bridge |
|---|---|---|---|---|
| `normality::shapiro_wilk` | `scipy.stats.shapiro` | MATCH ~1e-16 | MATCH ~1e-16 | ✅ trust both |
| `normality::jarque_bera` | `scipy.stats.jarque_bera` | MATCH ~1e-15 | MATCH ~1e-15 | ✅ trust both |
| `normality::anderson_darling` | `statsmodels.normal_ad` | MATCH ~1e-11 | MATCH ~1e-12 | ✅ trust both |
| `ljung_box` *(local)* | `statsmodels.acorr_ljungbox` | MATCH ~1e-13 | MATCH ~1e-13 | ✅ trust both |
| `ks_test_exact` *(local)* | `scipy.stats.kstest` | MATCH ~2e-11 | ~1.5e-5 (asymptotic) | ✅ accept/reject only\* |
| `henze_zirkler` *(via `joint_normality`)* | `pingouin.multivariate_normality` | MATCH ~1e-9 † | MATCH ~1e-9 † | ✅ trust both |
| `mardia` *(via `joint_normality`)* | numpy reimpl (no std Python test) | MATCH ~1e-12 † | MATCH ~1e-12 † | ✅ internally correct |
| `normality::lilliefors` | `statsmodels.lilliefors` | MATCH ~1e-16 | **DIVERGES ~8e-4** | ⚠ statistic only |
| `cramer_von_mises` *(local)* | `scipy.stats.cramervonmises` | MATCH ~1e-9 | **DOCKED (~6 orders)** | ⚠ statistic only |

\* KS p-value is the asymptotic Kolmogorov approximation; scipy uses an exact small-n method.
The gap (~1.5e-5 at n=30) widens for very small n — fine for accept/reject, not exact reporting.
† after the 2026-06-28 covariance-flag fix (below).

## Notes & fixes

### Shapiro–Wilk — no divergence (the good surprise)
The `normality` crate uses AS R94 (Royston), the same algorithm scipy uses; they agree to
~1e-16. We had expected an algorithm-variant gap — there isn't one.

### Henze–Zirkler + Mardia — covariance-flag fix (2026-06-28)
`joint_normality` originally passed the crate's `false` flag, which in `normality` 3.0.1 means
**sample** covariance (1/(n-1)) — diverging ~1.3e-2 from pingouin (population, 1/n). The inline
comment had the flag backwards. **Fixed:** flipped to `true` = population covariance (1/n), the
standard MVN/pingouin convention. HZ now matches pingouin to ~1e-9; Mardia matches the direct
`mardia(true)` call. Mardia has no scipy/statsmodels/pingouin test, so it's verified against a
faithful numpy reimplementation of the same formula (machine precision under the matching flag).

### Cramér–von Mises p-value — DOCKED (real bug, not fixed)
The W² **statistic** matches scipy to ~1e-9. The **p-value** applies the Stephens (1976)
estimated-parameter (case-3) correction *unconditionally* — wrong when σ is known (case-0, fully
specified): it returns ~7e-10 vs scipy's ~1e-3 (≈6 orders). The case-3 (estimated) path is also
untested. **Not fixed** — we deliberately did not introduce an unverified case-0 port. The bridge
uses the CvM statistic and computes the p-value in Python (scipy), or a verified case-0
distribution must be implemented here first. See the `DOCKED` comment in `cramer_von_mises`.

### Lilliefors p-value — minor divergence
Statistic matches statsmodels to ~1e-16. The p-value differs ~8e-4 — different approximation
families (the crate ports R `nortest`; statsmodels uses Dallal–Wilkinson + a 0.001-clamped
table). Bridge the statistic; use Python if exact Lilliefors p-values matter.

## Bridge guidance
- **Trust statistic + p-value:** `shapiro_wilk`, `jarque_bera`, `anderson_darling`, `ljung_box`,
  `henze_zirkler`, `mardia`; `ks_test_exact` p-value for accept/reject only.
- **Trust statistic, NOT p-value:** `cramer_von_mises` (docked), `lilliefors`.
- The future `fern-run diagnose` subcommand should emit the trusted set; for CvM/Lilliefors emit
  the statistic and either omit the p-value or recompute it in Python.
