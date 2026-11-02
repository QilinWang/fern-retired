using System.Text.Json;
using Fern.Site;

namespace Fern.Install;

// CHG 2026-09-12: split out of InstallationVerification.cs into its own file.
// It answers in the Worker Site's own result type, which is why this project
// references Fern.Site: the members an installation publishes are exactly what
// a Site executes, and its one production caller is composing a command for
// one. Fern.Site is a leaf that opens no Home, so the edge stays one way.
public sealed record InstalledControlMembers(
    string SiteBinaryPath,
    string RunBinaryPath,
    string CompileBinaryPath);

/// <summary>Reads an installation receipt for the paths a worker must execute.</summary>
public static class InstalledControlReader
{
    /// <summary>
    /// The three executables a worker machine runs, from the receipt at the
    /// installed root. A receipt that names no card compiler is refused by that
    /// name: without it a cell cannot ask for its configuration's identity, and
    /// "the installation is old" is not something a person can act on.
    /// </summary>
    public static Fern.Site.SiteResult<InstalledControlMembers> Members(string receiptPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(receiptPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Fern.Site.SiteResult<InstalledControlMembers>.Permanent(
                $"installation receipt {receiptPath} cannot be read: {error.Message}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException invalid)
        {
            return Fern.Site.SiteResult<InstalledControlMembers>.Permanent(
                $"installation receipt {receiptPath} is not valid JSON: {invalid.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema)
                || schema.GetString() != ControlPlaneInstallationContract.ReceiptSchema)
            {
                return Fern.Site.SiteResult<InstalledControlMembers>.Permanent(
                    $"installation receipt {receiptPath} is not a {ControlPlaneInstallationContract.ReceiptSchema} document");
            }

            var site = Member(root, "site_binary_path", "fern-site", receiptPath);
            if (!site.IsOk) return site.RefusalAs<InstalledControlMembers>();
            var run = Member(root, "run_binary_path", "fern-run", receiptPath);
            if (!run.IsOk) return run.RefusalAs<InstalledControlMembers>();
            var compile = Member(root, "compile_binary_path", "fern-compile", receiptPath);
            if (!compile.IsOk) return compile.RefusalAs<InstalledControlMembers>();

            return Fern.Site.SiteResult<InstalledControlMembers>.Ok(
                new InstalledControlMembers(site.Value, run.Value, compile.Value));
        }
    }

    static Fern.Site.SiteResult<string> Member(JsonElement root, string property, string name, string receiptPath)
    {
        if (!root.TryGetProperty(property, out var found)
            || found.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(found.GetString()))
        {
            return Fern.Site.SiteResult<string>.Permanent(
                $"installation receipt {receiptPath} names no {name} member; "
                + "install the control plane on this machine again");
        }

        var path = found.GetString()!;
        if (!File.Exists(path))
        {
            return Fern.Site.SiteResult<string>.Permanent(
                $"installation receipt {receiptPath} names {name} at {path}, which this machine does not hold");
        }
        return Fern.Site.SiteResult<string>.Ok(path);
    }
}
