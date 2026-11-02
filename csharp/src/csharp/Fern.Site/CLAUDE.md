@AGENTS.md

This file is an import shim, not a rules file. Claude Code auto-loads a nested
`CLAUDE.md` when work touches this directory; the `@AGENTS.md` line imports the
rules written for this subtree so a Claude session reads the same text Codex
and Grok already read from `AGENTS.md`. Do not add rules here — the sibling
`AGENTS.md` is their one home, the repository root's still applies, and history
lives in git.
