//! Shared artifact reopen check for Stage registration and execution.

use std::fs;
use std::path::{Path, PathBuf};

use fern_artifact::{CheckedArtifactV1, ReportedArtifactV1};

use crate::{Fallible, invalid_input};

