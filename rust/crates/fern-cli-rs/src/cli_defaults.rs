// CHG 2026-08-28: moved verbatim from crates/fern-statistics/src/repo_health/cli_defaults.rs
// when that crate retired (retired/crates/fern-statistics-2026-08-28); the rule it holds is
// about fern-run's own CLI, and its one caller is the cli_hygiene test in main.rs.
//! Does any command default a value that decides SCOPE or IDENTITY?
//!
//! WHY. Three failures on 2026-08-12, none of them an invalid value — all three
//! were a perfectly legal default doing something nobody asked for.
//!
//! - `--limit 20` on `list-runs`. A campaign-wide scan cannot know its own
//!   size, so the default silently answered a question about the first twenty
//!   rows and looked like an answer about ten thousand.
//! - `--python` defaulted to `"python"`, so "not supplied" and "supplied the
//!   placeholder" became the same input. A command that gave no interpreter was
//!   refused for disagreeing with the profile, and the branch that accepted it
//!   would have launched a worker with the literal string (correction 284).
//! - `--db` defaulted to `results/fern.db`, a database that stopped being
//!   authoritative. Every reader that took the default read a projection and
//!   reported an empty campaign.
//!
//! No lint catches this. A type system cannot: `20` is a fine usize and
//! `"python"` is a fine string. What separates these from `--format text` is
//! not the value, it is that the argument decides WHICH DATA the command reads
//! or WHOSE IDENTITY it runs under. So the rule is about the argument's job:
//!
//! > **An argument that selects the data, the machine, or the identity may not
//! > have a default. It is `Option<T>`, and the command decides — out loud —
//! > what its absence means.**
//!
//! Presentation arguments (`--format`, `--detail`, `--json`, `--quiet`) keep
//! their defaults. Getting those wrong changes what you read, not what runs.
//!
//! THE THREE RULES, and which of them this file can enforce:
//!
//! 1. No default on a scope- or identity-bearing argument. **Enforced here**,
//!    by walking the clap command tree.
//! 2. A default that does exist is a shared constant, not a literal repeated
//!    between the parser and the code that reads it. **Enforced here** for the
//!    arguments below, trivially: they have no default to duplicate.
//! 3. Omission must be distinguishable from the default value, and the command
//!    must say what it chose. **Not enforceable from the tree** — it is a
//!    property of the resolution code, and each command tests it directly.

/// Argument names whose value selects the data, the machine, or the identity.
///
/// Named rather than pattern-matched: a rule that guesses from the name would
/// catch `--registry` and miss `--on`, and a guessing rule that fires on
/// something harmless gets suppressed rather than fixed. Add a name here when
/// a new argument decides WHICH data or WHOSE identity — and if you are unsure,
/// ask what a wrong value would produce. A wrong `--format` is unreadable. A
/// wrong `--registry` is a confident answer about the wrong campaign.
pub const SCOPE_BEARING_ARGUMENTS: &[&str] = &[
    // Which data, where the default cannot be checked cheaply or points at a
    // source that has stopped being authoritative.
    "db", "source", "lane", "limit",
    // Which machine, and whose identity. Every one of these is a fact about the
    // world outside the repository; a baked value is right on one machine.
    "on", "python", "device", "profile", "actor_id",
    // Which experiment. A defaulted one runs something nobody named.
    "plan", "matrix", "attempt", "recipe", "preset", "model",
];

/// DELIBERATELY NOT ON THE LIST, with the reason, because a rule with a
/// suppressed failure is worse than no rule.
///
/// `root`, `campaign_root`, `registry`, `artifact_root` all default, and they
/// stay that way. They are not facts about a machine or a choice of experiment
/// — they are this repository's layout, and `.` really is the answer for every
/// invocation anyone makes. Requiring them on every command would be ceremony
/// paid on every line for a mistake nobody has made.
///
/// What makes them safe is not the default, it is that each is VERIFIED where
/// it is used and fails with the path it tried: `fern-home grid-gaps` refuses a campaign
/// root holding no Matrix, `list-runs --campaign-root` refuses a root holding
/// no lane registry, and the collision check refuses to call zero examined rows
/// a clean sweep. That is the property that actually mattered on 2026-08-12 —
/// the defaults were not the failure, the SILENT EMPTY ANSWER was.
///
/// If one of these ever starts selecting between two real campaigns rather than
/// naming this one, it belongs on the list above.
pub const DEFAULTED_BY_DESIGN: &[&str] = &["root", "campaign_root", "registry", "artifact_root"];

/// One argument that must not carry a default, and the value it carries.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct DefaultedArgument {
    /// Space-separated command path, e.g. `fern-run list-runs`.
    pub command: String,
    pub argument: String,
    pub default: String,
}

/// Every scope-bearing argument in this command tree that carries a default.
///
/// Walks subcommands recursively, because a rule that only inspects the root is
/// a rule that inspects nothing: every real command here is a subcommand.
pub fn defaulted_scope_arguments(command: &clap::Command) -> Vec<DefaultedArgument> {
    let mut found = Vec::new();
    walk(command, command.get_name().to_string(), &mut found);
    found.sort_by(|left, right| (&left.command, &left.argument).cmp(&(&right.command, &right.argument)));
    found
}

fn walk(command: &clap::Command, path: String, found: &mut Vec<DefaultedArgument>) {
    for argument in command.get_arguments() {
        let id = argument.get_id().as_str();
        if !SCOPE_BEARING_ARGUMENTS.contains(&id) {
            continue;
        }
        let defaults = argument.get_default_values();
        if defaults.is_empty() {
            continue;
        }
        found.push(DefaultedArgument {
            command: path.clone(),
            argument: id.to_string(),
            default: defaults
                .iter()
                .map(|value| value.to_string_lossy().to_string())
                .collect::<Vec<_>>()
                .join(", "),
        });
    }
    for subcommand in command.get_subcommands() {
        walk(subcommand, format!("{path} {}", subcommand.get_name()), found);
    }
}

/// The message a failing test prints: every offender, and what to do.
pub fn describe(found: &[DefaultedArgument]) -> String {
    if found.is_empty() {
        return "no scope-bearing argument carries a default".to_string();
    }
    let mut text = format!(
        "{} scope-bearing argument(s) carry a default. An argument that selects \
         the data, the machine, or the identity must be Option<T>, and the \
         command must say what its absence means:\n",
        found.len()
    );
    for entry in found {
        text.push_str(&format!(
            "\n  {}  --{}  defaults to {:?}",
            entry.command,
            entry.argument.replace('_', "-"),
            entry.default
        ));
    }
    text.push_str(
        "\n\nIf one of these does not in fact select data or identity, remove it \
         from SCOPE_BEARING_ARGUMENTS with the reason, rather than leaving a \
         suppressed failure.",
    );
    text
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::{Arg, Command};

    #[test]
    fn a_defaulted_scope_argument_is_found() {
        let command = Command::new("tool").arg(Arg::new("db").long("db").default_value("results/fern.db"));
        let found = defaulted_scope_arguments(&command);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].argument, "db");
        assert_eq!(found[0].default, "results/fern.db");
        assert!(describe(&found).contains("--db"));
    }

    /// Presentation arguments keep their defaults; the rule is about the job.
    #[test]
    fn a_defaulted_presentation_argument_is_left_alone() {
        let command = Command::new("tool").arg(Arg::new("format").long("format").default_value("text"));
        assert!(defaulted_scope_arguments(&command).is_empty());
    }

    /// Every real command in this repository is a subcommand, so a walker that
    /// stops at the root inspects nothing at all.
    #[test]
    fn a_nested_subcommand_is_walked() {
        let command = Command::new("tool").subcommand(
            Command::new("outer")
                .subcommand(Command::new("inner").arg(Arg::new("limit").long("limit").default_value("20"))),
        );
        let found = defaulted_scope_arguments(&command);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].command, "tool outer inner");
        assert_eq!(found[0].argument, "limit");
    }

    #[test]
    fn a_scope_argument_without_a_default_passes() {
        let command = Command::new("tool").arg(Arg::new("registry").long("registry"));
        assert!(defaulted_scope_arguments(&command).is_empty());
        assert_eq!(describe(&[]), "no scope-bearing argument carries a default");
    }
}
