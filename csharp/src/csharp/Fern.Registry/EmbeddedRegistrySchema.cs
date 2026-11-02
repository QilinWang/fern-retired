using System.Text;

namespace Fern.Registry;

/// <summary>The single reviewed current schema travels inside its executable.</summary>
public static class EmbeddedRegistrySchema
{
    public const string ResourceName = "fern.registry.schema.sql";
    static readonly Lazy<string> Definition = new(() =>
    {
        using var stream = typeof(EmbeddedRegistrySchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("the current Registry schema resource is missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        // Git checkout line endings do not create a different schema contract.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    });
    public static string Sql => Definition.Value;

    /// <summary>
    /// The label a Home stores for the schema it holds: its version, written
    /// so a person can read it. No decision reads it: a Home is current by its
    /// version stamp and its objects (RegistrySchema.RequireCurrent).
    /// CHG 2026-09-27: its objects are compared by structure, not text; a walk no
    /// longer rewrites the stored row, so what a Home holds is said from its stamp
    /// (<see cref="IdentityOf"/>). The canonical form of SQL text, used to compare
    /// stored definitions, is removed with the text comparison.
    /// CHG 2026-09-26: this was a SHA-256 over Schema.sql, comments included,
    /// and it was compared first, so a comment edit made every build refuse
    /// every Home. Once the check moved to the version and the objects, the
    /// hash decided nothing (owner: "this sha256 gymnastic is not doing
    /// anything"), so the label is the version.
    /// </summary>
    public static string Identity => IdentityOf(RegistrySchemaTransforms.Current);

    /// <summary>The label of the schema a Home stamped <paramref name="version"/> holds.</summary>
    public static string IdentityOf(long version) => $"registry-schema:version-{version}";
}
