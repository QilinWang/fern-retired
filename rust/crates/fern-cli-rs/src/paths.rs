/// Where installation publishes immutable binaries and receipts.
///
/// One constant so the flag's default and the registration-time lookup cannot
/// drift into naming two different directories. The worker reads this same
/// name to find `implementation-source-roots` without compiling the installer.
pub(crate) const DEFAULT_DEPLOYMENT_ROOT: &str = "deploy";
