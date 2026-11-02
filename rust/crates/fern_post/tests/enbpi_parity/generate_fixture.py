"""Generate the EnbPI aggregation parity fixture FROM THE FROZEN PYTHON.

Role and caller: this script writes `fixture.json`, read by
`fern_post/tests/enbpi_parity.rs`. Regenerate with:

    /home/user/.venv/bin/python fern_post/tests/enbpi_parity/generate_fixture.py

Why it runs the original bytes instead of restating the formula: a port's
"nothing was dropped" test written from the port agrees with the port, not
with the thing being replaced (corrections 293/295). So this extracts
`_compute_oob_residuals` textually from the retired reference
(`retired/fr_tr_exp/fr_tr_exp_with_enbpi_2026_07_01.py`), executes those
exact bytes with its one infrastructure dependency (`_collect_point_preds`,
which ran live models) replaced by precomputed prediction tensors — the same
boundary the registered-plan rebuild uses, where members' forecasts arrive as
committed artifacts. The quantile and band lines of Phase 4 are inline in
`run_enbpi`, so they are reproduced here by calling the same numpy operations
the original lines call, on the same arrays.

Input design, chosen to exercise every branch of the original:
  - B=3 members, N_train=10 windows, H=2, D=2, block_size=3 -> K=3 full
    blocks; window 9 sits past the last full block and must be DROPPED.
  - block 0 is out-of-bag for nobody: windows 0-2 must be SKIPPED.
  - block 1 is out-of-bag for member 1 only: single-member mean.
  - block 2 is out-of-bag for members 0 and 2: two-member mean.
  - alpha=0.1 over the resulting 6 residuals puts the quantile position at
    (6-1)*0.9 = 4.5, so the linear INTERPOLATION path is exercised, not just
    an exact index.
  - values snap to a 1/64 grid so float32 (the original's torch dtype) and
    the port's float64 agree to ~1e-7 instead of accumulating drift.
"""

import json
import pathlib

import numpy as np
import torch

REPO = pathlib.Path(__file__).resolve().parents[3]
RETIRED = REPO / "retired/fr_tr_exp/fr_tr_exp_with_enbpi_2026_07_01.py"
OUT = pathlib.Path(__file__).with_name("fixture.json")

B, N_TRAIN, H, D, BLOCK_SIZE, N_TEST, ALPHA = 3, 10, 2, 2, 3, 4, 0.1

rng = np.random.default_rng(20260813)


def grid_values(*shape):
    """float32 values on a 1/64 grid — exactly representable in f32 and f64."""
    return (rng.integers(-64, 65, size=shape) / 64.0).astype(np.float32)


member_train_preds = grid_values(B, N_TRAIN, H, D)
train_true = grid_values(N_TRAIN, H, D)
member_test_preds = grid_values(B, N_TEST, H, D)
test_true = grid_values(N_TEST, H, D)
# [B][K] bool, True = block is out-of-bag for that member (the original's
# mask convention: `True=OOB`).
oob_block_masks = np.array(
    [
        [False, False, True],
        [False, True, False],
        [False, False, True],
    ]
)

# ── Run the ORIGINAL residual function, byte-for-byte ──
source = RETIRED.read_text()
start = source.index("def _compute_oob_residuals")
end = source.index("\n# ==", start)
original = source[start:end]
assert "return residuals" in original, "extraction lost the function body"

train_preds_tensors = [torch.from_numpy(member_train_preds[b]) for b in range(B)]


class TargetOnlyDataset:
    """The two things the original asks of `orig_train_dataset`."""

    def __len__(self):
        return N_TRAIN

    def __getitem__(self, i):
        return (None, torch.from_numpy(train_true[i]))


namespace = {
    "torch": torch,
    "np": np,
    # The one infrastructure dependency, replaced with data at the same
    # boundary the rebuild uses. Models are member ordinals.
    "_collect_point_preds": lambda m, dataset, batch_size=64, device="cuda": train_preds_tensors[m],
    "print": lambda *args, **kwargs: None,
}
exec(compile(original, str(RETIRED), "exec"), namespace)
residuals = namespace["_compute_oob_residuals"](
    models=list(range(B)),
    oob_masks=[torch.from_numpy(oob_block_masks[b]) for b in range(B)],
    orig_train_dataset=TargetOnlyDataset(),
    block_size=BLOCK_SIZE,
    device="cpu",
)
assert residuals.shape == (6,), f"expected 6 OOB residuals, got {residuals.shape}"

# ── Phase 4 of run_enbpi, the same numpy operations on the same arrays ──
quantile_width = float(np.quantile(residuals, 1 - ALPHA))
ensemble_pred = torch.stack([torch.from_numpy(member_test_preds[b]) for b in range(B)]).mean(dim=0).numpy()
lower = ensemble_pred - quantile_width
upper = ensemble_pred + quantile_width

OUT.write_text(
    json.dumps(
        {
            "member_train_preds": member_train_preds.astype(np.float64).tolist(),
            "train_true": train_true.astype(np.float64).tolist(),
            "oob_block_masks": oob_block_masks.tolist(),
            "block_size": BLOCK_SIZE,
            "member_test_preds": member_test_preds.astype(np.float64).tolist(),
            "test_true": test_true.astype(np.float64).tolist(),
            "alpha": ALPHA,
            "expected_oob_residuals": residuals.astype(np.float64).tolist(),
            "expected_quantile_width": quantile_width,
            "expected_test_pred": ensemble_pred.astype(np.float64).tolist(),
            "expected_lower": lower.astype(np.float64).tolist(),
            "expected_upper": upper.astype(np.float64).tolist(),
        },
        indent=1,
    )
)
print(f"wrote {OUT} ({len(residuals)} residuals, q={quantile_width})")
