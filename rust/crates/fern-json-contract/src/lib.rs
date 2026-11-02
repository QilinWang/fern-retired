//! One generated JSON contract for every durable or machine-facing boundary.
//!
//! A string field named `schema` is useful routing metadata, but it is not by
//! itself a contract. A Fern boundary type implements [`JsonContract`], which
//! gives it one stable schema id, a JSON Schema generated from the Rust type,
//! and a checked serialization path that refuses an incorrect or missing
//! top-level schema tag.

use schemars::JsonSchema;
use serde::{Serialize, de::DeserializeOwned};
use serde_json::Value;

/// Error raised when a Rust value and its declared JSON contract disagree.
#[derive(Debug)]
pub struct JsonContractError(String);

impl std::fmt::Display for JsonContractError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl std::error::Error for JsonContractError {}

impl From<serde_json::Error> for JsonContractError {
    fn from(error: serde_json::Error) -> Self {
        Self(error.to_string())
    }
}

/// Decode one machine-facing JSON value through its Rust type and schema tag.
///
/// `Deserialize` enforces the typed shape (and `deny_unknown_fields` where the
/// boundary declares it); [`JsonContract::checked_value`] then proves that the
/// document selected the same versioned contract as that Rust type.
pub fn checked_from_value<T>(value: Value) -> Result<T, JsonContractError>
where
    T: DeserializeOwned + JsonContract,
{
    let decoded: T = serde_json::from_value(value)?;
    decoded.checked_value()?;
    Ok(decoded)
}

/// Decode a JSON byte stream through the same generated contract boundary.
pub fn checked_from_slice<T>(bytes: &[u8]) -> Result<T, JsonContractError>
where
    T: DeserializeOwned + JsonContract,
{
    checked_from_value(serde_json::from_slice(bytes)?)
}

/// Encode JSON with object keys sorted at every level.
///
/// Identity producers and consumers must call this one function; ordinary
/// `serde_json::to_vec` preserves struct field order and therefore is not a
/// canonical content-addressing format.
pub fn canonical_json_bytes<T: Serialize>(value: &T) -> Result<Vec<u8>, JsonContractError> {
    fn write(value: &Value, output: &mut Vec<u8>) -> Result<(), JsonContractError> {
        match value {
            Value::Null => output.extend_from_slice(b"null"),
            Value::Bool(value) => output.extend_from_slice(if *value { b"true" } else { b"false" }),
            Value::Number(value) => output.extend_from_slice(value.to_string().as_bytes()),
            Value::String(value) => output.extend_from_slice(serde_json::to_string(value)?.as_bytes()),
            Value::Array(values) => {
                output.push(b'[');
                for (index, value) in values.iter().enumerate() {
                    if index > 0 {
                        output.push(b',');
                    }
                    write(value, output)?;
                }
                output.push(b']');
            }
            Value::Object(values) => {
                output.push(b'{');
                let mut entries = values.iter().collect::<Vec<_>>();
                entries.sort_by(|left, right| left.0.cmp(right.0));
                for (index, (key, value)) in entries.into_iter().enumerate() {
                    if index > 0 {
                        output.push(b',');
                    }
                    output.extend_from_slice(serde_json::to_string(key)?.as_bytes());
                    output.push(b':');
                    write(value, output)?;
                }
                output.push(b'}');
            }
        }
        Ok(())
    }

    let value = serde_json::to_value(value)?;
    let mut output = Vec::new();
    write(&value, &mut output)?;
    Ok(output)
}

/// A versioned Fern JSON object whose schema comes from its Rust type.
pub trait JsonContract: Serialize + JsonSchema {
    /// Stable identity carried in the serialized object's top-level `schema`.
    const SCHEMA_ID: &'static str;

    /// Generate a JSON Schema document with the same stable identity as `$id`.
    fn schema_document() -> Result<Value, JsonContractError> {
        let mut value = serde_json::to_value(schemars::schema_for!(Self))?;
        let object = value.as_object_mut().ok_or_else(|| {
            JsonContractError(format!("generated schema for {} is not a JSON object", Self::SCHEMA_ID))
        })?;
        object.insert("$id".to_string(), Value::String(Self::SCHEMA_ID.to_string()));
        if let Some(schema_property) = object
            .get_mut("properties")
            .and_then(Value::as_object_mut)
            .and_then(|properties| properties.get_mut("schema"))
            .and_then(Value::as_object_mut)
        {
            schema_property.insert("const".to_string(), Value::String(Self::SCHEMA_ID.to_string()));
        }
        Ok(value)
    }

    /// Serialize and prove the top-level schema tag matches the Rust contract.
    fn checked_value(&self) -> Result<Value, JsonContractError> {
        let value = serde_json::to_value(self)?;
        let observed = value.get("schema").and_then(Value::as_str);
        if observed != Some(Self::SCHEMA_ID) {
            return Err(JsonContractError(format!(
                "{} serialized with schema tag {:?}; expected {:?}",
                std::any::type_name::<Self>(),
                observed,
                Self::SCHEMA_ID
            )));
        }
        Ok(value)
    }

    /// Pretty JSON for human terminals and machine clients through one check.
    fn checked_pretty_json(&self) -> Result<String, JsonContractError> {
        Ok(serde_json::to_string_pretty(&self.checked_value()?)?)
    }
}

/// One entry in a CLI/provider schema registry.
#[derive(Clone, Copy)]
pub struct ContractRegistration {
    pub schema_id: &'static str,
    pub rust_type: &'static str,
    pub description: &'static str,
    pub schema_document: fn() -> Result<Value, JsonContractError>,
}

impl ContractRegistration {
    /// Build one registry row from a concrete contract type.
    pub fn of<T: JsonContract>(description: &'static str) -> Self {
        Self {
            schema_id: T::SCHEMA_ID,
            rust_type: std::any::type_name::<T>(),
            description,
            schema_document: T::schema_document,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::{Deserialize, Serialize};

    #[derive(Debug, Serialize, Deserialize, JsonSchema)]
    #[serde(deny_unknown_fields)]
    struct Example {
        schema: String,
        count: u64,
    }

    impl JsonContract for Example {
        const SCHEMA_ID: &'static str = "fern.example.v1";
    }

    #[test]
    fn generated_schema_and_runtime_tag_share_one_identity() {
        let schema = Example::schema_document().unwrap();
        assert_eq!(schema["$id"], "fern.example.v1");
        assert_eq!(schema["properties"]["schema"]["const"], "fern.example.v1");
        let value = Example {
            schema: Example::SCHEMA_ID.to_string(),
            count: 3,
        }
        .checked_value()
        .unwrap();
        assert_eq!(value["schema"], schema["$id"]);
    }

    #[test]
    fn incorrect_runtime_tag_is_refused() {
        let error = Example {
            schema: "fern.wrong.v1".to_string(),
            count: 3,
        }
        .checked_value()
        .unwrap_err()
        .to_string();
        assert!(error.contains("fern.wrong.v1"));
        assert!(error.contains("fern.example.v1"));
    }

    #[test]
    fn typed_reader_refuses_unknown_fields_and_wrong_schema() {
        let unknown = checked_from_slice::<Example>(br#"{"schema":"fern.example.v1","count":3,"surprise":true}"#)
            .unwrap_err()
            .to_string();
        assert!(unknown.contains("unknown field"));

        let wrong = checked_from_slice::<Example>(br#"{"schema":"fern.wrong.v1","count":3}"#)
            .unwrap_err()
            .to_string();
        assert!(wrong.contains("fern.wrong.v1"));
    }

    #[test]
    fn canonical_json_is_independent_of_object_insertion_order() {
        let left = serde_json::json!({"z": 1, "nested": {"b": 2, "a": 1}});
        let right = serde_json::json!({"nested": {"a": 1, "b": 2}, "z": 1});
        assert_eq!(
            canonical_json_bytes(&left).unwrap(),
            canonical_json_bytes(&right).unwrap()
        );
    }

    #[test]
    fn canonical_json_sorts_struct_fields_that_plain_serde_json_leaves_declared() {
        // This is the property the three committed fern-artifact call sites
        // rely on: they content-address these bytes, so two encoders that
        // disagree about key order mint two identities for one value.
        //
        // The neighbouring insertion-order test cannot fail on its own, because
        // serde_json's Map is a BTreeMap unless `preserve_order` is enabled, so
        // it is sorted before this function ever sees it. A struct is the case
        // that separates the two encoders, because serde writes struct fields
        // in declaration order without going through a Map at all.
        #[derive(Serialize)]
        struct DeclaredOutOfOrder {
            zulu: u8,
            alpha: u8,
        }

        let value = DeclaredOutOfOrder { zulu: 1, alpha: 2 };
        assert_eq!(
            String::from_utf8(serde_json::to_vec(&value).unwrap()).unwrap(),
            r#"{"zulu":1,"alpha":2}"#,
            "plain serde_json follows declaration order and so cannot address content"
        );
        assert_eq!(
            String::from_utf8(canonical_json_bytes(&value).unwrap()).unwrap(),
            r#"{"alpha":2,"zulu":1}"#
        );
    }
}
