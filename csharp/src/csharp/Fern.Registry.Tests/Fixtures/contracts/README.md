# Registry contract fixtures

These files are active, implementation-independent Registry contract inputs.
They were copied byte-for-byte from the fixtures preserved with the retired
Rust crates on 2026-08-31. The Rust origin records provenance, not current
authority: Registry tests read only this directory and do not fall back to
`retired/` or Git history.

Do not regenerate these files from the C# implementation under test. A public
contract change must update the fixture and the consuming assertion together.
