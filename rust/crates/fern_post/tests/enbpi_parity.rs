//! EnbPI aggregation parity against the FROZEN Python reference.
//!
//! `enbpi_parity/fixture.json` is written by `enbpi_parity/generate_fixture.py`,
//! which executes the retired `_compute_oob_residuals` byte-for-byte (and the
//! same numpy calls as `run_enbpi`'s Phase 4) on small deterministic inputs —
//! so the expected values here come from the thing being replaced, not from
//! the port (corrections 293/295). The fixture's inputs exercise the dropped
//! tail window, the nobody-holds-it-out skip, single- and two-member means,
//! and the interpolated quantile position.
//!
//! Tolerance: the original computes residuals and the ensemble in torch
//! float32; the port uses f64 throughout. Inputs snap to a 1/64 grid so the
//! disagreement is division order only, bounded well under 1e-6; a formula
//! error moves these values by more than 1e-3.

use fern_post::diagnostics::EnbPIResult;
use ndarray::{Array1, Array3};

fn array3(values: &serde_json::Value) -> Array3<f64> {
    let outer = values.as_array().unwrap();
    let mid = outer[0].as_array().unwrap();
    let inner = mid[0].as_array().unwrap();
    let mut flat = Vec::new();
    for a in outer {
        for b in a.as_array().unwrap() {
            for c in b.as_array().unwrap() {
                flat.push(c.as_f64().unwrap());
            }
        }
    }
    Array3::from_shape_vec((outer.len(), mid.len(), inner.len()), flat).unwrap()
}

fn close(left: &Array3<f64>, right: &Array3<f64>, what: &str) {
    for (l, r) in left.iter().zip(right.iter()) {
        assert!((l - r).abs() < 1e-6, "{what}: {l} vs {r}");
    }
}

#[test]
fn the_port_reproduces_the_frozen_python_on_the_fixture() {
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("enbpi_parity/fixture.json")).expect("fixture parses");

    let member_train_preds: Vec<Array3<f64>> = fixture["member_train_preds"]
        .as_array()
        .unwrap()
        .iter()
        .map(array3)
        .collect();
    let member_test_preds: Vec<Array3<f64>> = fixture["member_test_preds"]
        .as_array()
        .unwrap()
        .iter()
        .map(array3)
        .collect();
    let oob_block_masks: Vec<Vec<bool>> = fixture["oob_block_masks"]
        .as_array()
        .unwrap()
        .iter()
        .map(|row| row.as_array().unwrap().iter().map(|v| v.as_bool().unwrap()).collect())
        .collect();
    let train_true = array3(&fixture["train_true"]);
    let test_true = array3(&fixture["test_true"]);

    let result = EnbPIResult::from_member_forecasts(
        &member_train_preds,
        &train_true,
        &oob_block_masks,
        fixture["block_size"].as_u64().unwrap() as usize,
        &member_test_preds,
        test_true,
        fixture["alpha"].as_f64().unwrap(),
        "parity_fixture".to_string(),
    )
    .expect("the fixture is a valid EnbPI input");

    let expected_residuals: Vec<f64> = fixture["expected_oob_residuals"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| v.as_f64().unwrap())
        .collect();
    assert_eq!(
        result.oob_residuals.len(),
        expected_residuals.len(),
        "residual COUNT is structural (skip and tail-drop rules), not floating point"
    );
    for (ours, theirs) in result.oob_residuals.iter().zip(&expected_residuals) {
        assert!((ours - theirs).abs() < 1e-6, "residual {ours} vs {theirs}");
    }
    let expected_quantile = fixture["expected_quantile_width"].as_f64().unwrap();
    assert!(
        (result.quantile_width - expected_quantile).abs() < 1e-6,
        "quantile {} vs {expected_quantile}",
        result.quantile_width
    );
    close(
        &result.test_pred,
        &array3(&fixture["expected_test_pred"]),
        "ensemble forecast",
    );
    close(&result.lower, &array3(&fixture["expected_lower"]), "lower band");
    close(&result.upper, &array3(&fixture["expected_upper"]), "upper band");

    let _ = Array1::<f64>::zeros(0); // keep the Array1 import honest if assertions above change
}

#[test]
fn a_grid_where_no_member_holds_anything_out_is_refused() {
    // The failure path exists because a mask of all-false makes intervals
    // impossible, and silence here would ship a band of width NaN.
    let member_train = vec![Array3::<f64>::zeros((3, 1, 1))];
    let member_test = vec![Array3::<f64>::zeros((2, 1, 1))];
    let error = EnbPIResult::from_member_forecasts(
        &member_train,
        &Array3::<f64>::zeros((3, 1, 1)),
        &[vec![false, false, false]],
        1,
        &member_test,
        Array3::<f64>::zeros((2, 1, 1)),
        0.1,
        "refusal".to_string(),
    )
    .unwrap_err();
    assert!(error.contains("out-of-bag for any member"), "{error}");
}
