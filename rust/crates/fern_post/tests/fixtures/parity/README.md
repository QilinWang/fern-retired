# The three inputs `fern_post/PARITY.md` was measured on

`../../../PARITY.md` records which of Fern's Rust diagnostics may be trusted
against scipy, statsmodels and pingouin, measured on 2026-06-28. Until
2026-08-22 the inputs it was measured on lived only in a session scratchpad, so
the record could be read but not re-run by anyone else. These are those inputs.

| file | shape | what it is |
|---|---|---|
| `normal_sample_n30.txt` | 30 values, one per line | a normal sample with a KNOWN standard deviation of 2.0, which is what makes the Kolmogorov-Smirnov and Cramer-von Mises tests on it fully specified (case 0) |
| `skewed_sample_n30.txt` | 30 values, one per line | a skewed (exponential) sample, the non-normal side of every normality comparison |
| `correlated_matrix_40x3.txt` | 40 rows of 3 space-separated values | a correlated multivariate-normal matrix, the input to Henze-Zirkler and Mardia |

## Where these bytes came from

They are **not** re-derived on a new seed. They were recovered on 2026-08-22
from the literals in `fern_post/src/diagnostics.rs`'s `#[cfg(test)] mod parity`
— `normal_sample()`, `nonnormal_sample()` and `mv_matrix()` — which PARITY.md
states are a bit-identical `repr()` round-trip of the original scratchpad
inputs. Each value here is Python's shortest round-trip decimal, character for
character the same string that appears in the Rust source, and Rust's `f64`
parse is correctly rounded, so the files and the literals are the same bits.

The original generator (`gen_inputs.py`) and reference script (`reference.py`)
were in that scratchpad and were not recovered. The values were, which is what
the record depends on.

## What keeps them honest

Two tests, one on each side, and neither of them takes this file's word for it:

- **Rust** — `mod parity::the_committed_fixture_files_hold_exactly_these_three_inputs`
  in `fern_post/src/diagnostics.rs` reads these files and compares them value by
  value against the inline literals. The literals are the party to that
  comparison that cannot be edited into agreement by whoever regenerates a
  fixture.
  Run: `fern-go lane cargo -- nextest run -p fern_post -E 'test(parity)'`
- **Python** — `tests/test_parity_fixtures.py` reads the same three files and
  re-runs every scipy, statsmodels and pingouin reference the record was
  measured against, asserting the numbers PARITY.md carries.
  Run: `/home/user/.venv/bin/python -m pytest tests/test_parity_fixtures.py -q`

Do not edit these files. A changed value silently moves every number in
PARITY.md; if a new input is needed, add a new file and a new record beside it.
