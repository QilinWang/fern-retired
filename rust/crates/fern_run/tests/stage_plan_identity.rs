//! Stage-plan identity hashing: empty `paired_unit_id` is omitted, set
//! digests sort first, and stored campaign Home rows recompute.

use fern_run::lifecycle::sha256_hex;
use fern_run::stage_attempt::{
    RegisteredStagePlanInstance, StagePlanInstanceInput, instance_input_set_digest, stage_plan_instance_id,
    stage_plan_set_digest,
};

const LEGACY_ONE_CELL: &str = "legacy-one-cell";
const FIXTURE: &str = include_str!("fixtures/stage_plan_identity_campaign_home.json");

#[derive(serde::Deserialize)]
struct StoredInput {
    input_key: String,
    sha256: String,
    path: String,
    byte_count: i64,
    role: String,
    schema_id: String,
}

#[derive(serde::Deserialize)]
struct StoredInstance {
    plan_id: String,
    stage_plan_instance_id: String,
    cell_ordinal: i64,
    compiled_cell_subject_digest: String,
    stage_plan_digest: String,
    training_worker_request_digest: String,
    target_hash: String,
    config_hash: String,
    paired_unit_id: String,
    #[serde(default)]
    stored_stage_plan_set_digest: Option<String>,
    inputs: Vec<StoredInput>,
}

fn digest_material(material: &serde_json::Value) -> String {
    let bytes = fern_json_contract::canonical_json_bytes(material).unwrap();
    format!("sha256:{}", sha256_hex(&bytes))
}

fn record(instance_id: &str, cell_ordinal: i64, paired_unit_id: &str, subject: &str) -> RegisteredStagePlanInstance {
    RegisteredStagePlanInstance {
        stage_plan_instance_id: instance_id.to_string(),
        cell_ordinal,
        compiled_cell_subject_digest: subject.to_string(),
        target_hash: "target".to_string(),
        config_hash: "config".to_string(),
        stage_plan_digest: "sha256:plan".to_string(),
        training_worker_request_digest: "sha256:req".to_string(),
        paired_unit_id: paired_unit_id.to_string(),
    }
}

#[test]
fn empty_paired_unit_id_is_omitted_and_matches_unpaired() {
    let unpaired = stage_plan_instance_id("cell", 0, "target", "config", "").unwrap();
    let empty_paired = stage_plan_instance_id("cell", 0, "target", "config", "").unwrap();
    assert_eq!(unpaired, empty_paired);

    let omitted = serde_json::json!({
        "compiled_cell_subject_digest": "cell",
        "cell_ordinal": 0,
        "target_hash": "target",
        "config_hash": "config",
    });
    let always_written = serde_json::json!({
        "compiled_cell_subject_digest": "cell",
        "cell_ordinal": 0,
        "target_hash": "target",
        "config_hash": "config",
        "paired_unit_id": "",
    });
    assert_eq!(unpaired, digest_material(&omitted));
    assert_ne!(unpaired, digest_material(&always_written));
    assert!(
        !fern_json_contract::canonical_json_bytes(&omitted)
            .unwrap()
            .windows(b"paired_unit_id".len())
            .any(|window| window == b"paired_unit_id")
    );
}

#[test]
fn a_non_empty_paired_unit_id_is_a_fifth_key() {
    let unpaired = stage_plan_instance_id("cell", 0, "target", "config", "").unwrap();
    let paired = stage_plan_instance_id("cell", 0, "target", "config", "pair-1").unwrap();
    assert_ne!(unpaired, paired);
    assert!(paired.starts_with("sha256:"));
}

#[test]
fn stage_plan_set_digest_sorts_by_cell_ordinal_and_omits_empty_paired_unit_id() {
    let first = record("sha256:b", 1, "", "cell-b");
    let second = record("sha256:a", 0, "pair", "cell-a");
    let forward = stage_plan_set_digest(&[second.clone(), first.clone()]).unwrap();
    let reverse = stage_plan_set_digest(&[first, second]).unwrap();
    assert_eq!(forward, reverse);
}

#[test]
fn instance_input_set_digest_sorts_keys_and_normalises_the_sha256_prefix() {
    let bare = vec![
        StagePlanInstanceInput {
            input_key: "zeta".to_string(),
            path: "z".to_string(),
            sha256: "abcd".to_string(),
            byte_count: 1,
            role: "role".to_string(),
            schema_id: "schema".to_string(),
        },
        StagePlanInstanceInput {
            input_key: "alpha".to_string(),
            path: "a".to_string(),
            sha256: "sha256:abcd".to_string(),
            byte_count: 1,
            role: "role".to_string(),
            schema_id: "schema".to_string(),
        },
    ];
    let prefixed = vec![
        StagePlanInstanceInput {
            input_key: "alpha".to_string(),
            path: "a".to_string(),
            sha256: "sha256:abcd".to_string(),
            byte_count: 1,
            role: "role".to_string(),
            schema_id: "schema".to_string(),
        },
        StagePlanInstanceInput {
            input_key: "zeta".to_string(),
            path: "z".to_string(),
            sha256: "sha256:abcd".to_string(),
            byte_count: 1,
            role: "role".to_string(),
            schema_id: "schema".to_string(),
        },
    ];
    assert_eq!(
        instance_input_set_digest(&bare).unwrap(),
        instance_input_set_digest(&prefixed).unwrap()
    );
}

#[test]
fn a_single_training_worker_request_is_the_prefixed_input_sha256() {
    let inputs = vec![StagePlanInstanceInput {
        input_key: "training_worker_request".to_string(),
        path: "req.json".to_string(),
        sha256: "deadbeef".to_string(),
        byte_count: 4,
        role: "role".to_string(),
        schema_id: "schema".to_string(),
    }];
    assert_eq!(instance_input_set_digest(&inputs).unwrap(), "sha256:deadbeef");
}

#[test]
fn campaign_home_stored_identities_recompute() {
    let rows: Vec<StoredInstance> = serde_json::from_str(FIXTURE).unwrap();
    assert_eq!(rows.len(), 40, "campaign Home held 40 Stage-plan instances at capture");

    let mut subject_reproduced = 0usize;
    let mut subject_known = 0usize;
    let mut subject_unexplained = 0usize;
    let mut inputs_reproduced = 0usize;
    let mut inputs_unexplained = 0usize;
    for row in &rows {
        let recomputed = stage_plan_instance_id(
            &row.compiled_cell_subject_digest,
            row.cell_ordinal,
            &row.target_hash,
            &row.config_hash,
            &row.paired_unit_id,
        )
        .unwrap();
        if recomputed == row.stage_plan_instance_id {
            subject_reproduced += 1;
        } else if row.stage_plan_instance_id == LEGACY_ONE_CELL {
            subject_known += 1;
        } else {
            subject_unexplained += 1;
        }

        let inputs: Vec<StagePlanInstanceInput> = row
            .inputs
            .iter()
            .map(|input| StagePlanInstanceInput {
                input_key: input.input_key.clone(),
                path: input.path.clone(),
                sha256: input.sha256.clone(),
                byte_count: input.byte_count,
                role: input.role.clone(),
                schema_id: input.schema_id.clone(),
            })
            .collect();
        let recomputed_inputs = instance_input_set_digest(&inputs).unwrap();
        if recomputed_inputs == row.training_worker_request_digest {
            inputs_reproduced += 1;
        } else {
            inputs_unexplained += 1;
        }
    }

    let mut by_plan: std::collections::BTreeMap<String, Vec<&StoredInstance>> = std::collections::BTreeMap::new();
    for row in &rows {
        by_plan.entry(row.plan_id.clone()).or_default().push(row);
    }
    let mut set_reproduced = 0usize;
    let mut set_unexplained = 0usize;
    let mut set_plans = 0usize;
    for instances in by_plan.values() {
        let Some(stored) = instances
            .first()
            .and_then(|row| row.stored_stage_plan_set_digest.as_deref())
        else {
            continue;
        };
        set_plans += 1;
        let records: Vec<RegisteredStagePlanInstance> = instances
            .iter()
            .map(|row| RegisteredStagePlanInstance {
                stage_plan_instance_id: row.stage_plan_instance_id.clone(),
                cell_ordinal: row.cell_ordinal,
                compiled_cell_subject_digest: row.compiled_cell_subject_digest.clone(),
                target_hash: row.target_hash.clone(),
                config_hash: row.config_hash.clone(),
                stage_plan_digest: row.stage_plan_digest.clone(),
                training_worker_request_digest: row.training_worker_request_digest.clone(),
                paired_unit_id: row.paired_unit_id.clone(),
            })
            .collect();
        let recomputed = stage_plan_set_digest(&records).unwrap();
        if recomputed == stored {
            set_reproduced += 1;
        } else {
            set_unexplained += 1;
        }
    }

    assert_eq!(subject_reproduced, 37);
    assert_eq!(subject_known, 3);
    assert_eq!(subject_unexplained, 0);
    assert_eq!(inputs_reproduced, 40);
    assert_eq!(inputs_unexplained, 0);
    assert_eq!(set_plans, 37);
    assert_eq!(set_reproduced, 37);
    assert_eq!(set_unexplained, 0);
}
