namespace Fern.Registry;

/// <summary>
/// Where this repository keeps the directories the control plane finds by a
/// path relative to the repository root.
///
/// The owner rearranged the tree on 2026-09-26. Source moved under <c>src/</c>:
/// the C# solution from <c>csharp/</c> to <c>src/csharp/</c> and the Serie
/// package from <c>serie/</c> to <c>src/serie/</c>. Declarations a person
/// writes moved under <c>config/</c>: <c>profiles/</c> became
/// <c>config/profiles/</c>. What the machine writes while it runs moved under
/// <c>runtime/</c>: <c>deploy/</c> (the installed binaries, their receipts,
/// the runtime selection and the installed study sources) became
/// <c>runtime/deploy/</c>, the Registry Home <c>lifecycle.db</c> became
/// <c>runtime/lifecycle.db</c>, and <c>store/</c>, <c>work/</c> and
/// <c>tmp/</c> became <c>runtime/store/</c>, <c>runtime/work/</c> and
/// <c>runtime/tmp/</c>. The same relative paths had been written out in some
/// thirty places across the installer, the scheduler and the operator
/// commands; they are written here once, and the places that read them name
/// these constants. Paths use '/' and carry no trailing slash.
/// </summary>
public static class RepositoryLayout
{
    /// <summary>The C# solution directory: every project, the shared props files and <c>Fern.sln</c>.</summary>
    public const string CSharpDirectory = "src/csharp";

    /// <summary>
    /// The default deployment root: content-addressed control binaries and
    /// their receipts, the runtime selection under <c>current/</c>, and the
    /// installed study-source views.
    /// </summary>
    public const string DeployDirectory = "runtime/deploy";

    /// <summary>The execution profiles and supervisor declarations, one JSON file per Site.</summary>
    public const string ProfilesDirectory = "config/profiles";

    /// <summary>The Registry Home database this repository's control plane keeps.</summary>
    public const string RegistryFile = "runtime/lifecycle.db";

    /// <summary>The content-addressed source and checkpoint store beside the Registry Home.</summary>
    public const string StoreDirectory = "runtime/store";

    /// <summary>The control plane's own work root, which install-control-plane derives from its root.</summary>
    public const string WorkDirectory = "runtime/work";

    /// <summary>The default attempt handoff and worker work root of the operator commands.</summary>
    public const string WorkerWorkRoot = "runtime/tmp/fern-run-worker";

    /// <summary>
    /// Whether a directory is the repository root, told by the one file only
    /// the root has below it: the C# solution at <c>src/csharp/Fern.sln</c>.
    /// A walk upward that tested for <c>csharp/Fern.sln</c> would now stop at
    /// <c>src/</c>, one level short of the root, and every path it then built
    /// from that "root" would name a directory that does not exist.
    /// </summary>
    public static bool IsRoot(string directory) =>
        File.Exists(Path.Combine(directory, "src", "csharp", "Fern.sln"));
}
