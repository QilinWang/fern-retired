using System.Text.Json;
using System.Text.Json.Serialization;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// Register one compiled study on the one-store schema: a `studies` row and one
/// `cells` row per compiled cell.
///
/// There is NO LOWERING here, and that absence is the point of the slice. The
/// old route turned each compiled cell into a Stage Plan — a graph of stages
/// with handlers, ports and digests — for a scheduler that could place stages
/// independently. Every plan the live route registered had exactly one stage,
/// so the graph had one node per graph and translated nothing. A cell row now
/// carries what that graph carried and a person can read: the configuration
/// document, the sha of the bytes the Site will be given, what the cell needs
/// to run, and what of its output is kept.
/// </summary>
public static class StudyRegistration
{
    /// <summary>The compiled study a registration reads, and what it refuses.</summary>
    public const string CompiledStudySchema = "fern.compiled-study.v1";

    /// <summary>
    /// Explicit placement declarations, checked without registering anything.
    /// They name the handler and Site capabilities rather than inferring them
    /// from the current Registry, which does not store allowed_handlers.
    /// </summary>
    public sealed record PlacementDeclarations(IReadOnlyList<PlaceableCell> Cells, IReadOnlyList<PlaceableSite> Sites);

    public static RegistryResult<PlacementResolution> CheckPlacement(string declarationsJson, GleamCellAdmission admission)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        PlacementDeclarations? declared;
        try
        {
            declared = JsonSerializer.Deserialize<PlacementDeclarations>(declarationsJson, options);
        }
        catch (JsonException error)
        {
            return RegistryResult<PlacementResolution>.Rejected(new("refused", "invalid_request", error.Message));
        }
        if (declared is null
            || declared.Cells.Any(cell => cell is null || string.IsNullOrWhiteSpace(cell.CellId)
                || string.IsNullOrWhiteSpace(cell.Model) || string.IsNullOrWhiteSpace(cell.StageHandler))
            || declared.Sites.Any(site => site is null || string.IsNullOrWhiteSpace(site.SiteId)
                || site.AdmittedModels.Any(string.IsNullOrWhiteSpace) || site.AllowedHandlers.Any(string.IsNullOrWhiteSpace))
            || declared.Cells.Select(cell => cell.CellId).Distinct(StringComparer.Ordinal).Count() != declared.Cells.Count
            || declared.Sites.Select(site => site.SiteId).Distinct(StringComparer.Ordinal).Count() != declared.Sites.Count)
        {
            return RegistryResult<PlacementResolution>.Rejected(new("refused", "invalid_request",
                "placement declarations require nonempty names and unique cell and Site ids"));
        }
        // One request for the whole declaration, never one per cell/Site pair.
        return admission.Resolve(declared.Cells, declared.Sites);
    }

    /// <summary>
    /// Read a compiled study and register it. The source must already be
    /// registered: a study is bound to the bytes it will run, and registering
    /// the two together would let a study exist with no source to run from.
    /// </summary>
    public static RegistryResult<RegistryOperations.RegisteredStudy> Register(
        RegistryWriter writer, string compiledStudyJson, string name, long revision, long sourceId, string device)
    {
        var checkedCells = CheckCompiledStudy(compiledStudyJson, device);
        if (!checkedCells.TryGetValue(out var declared))
        {
            checkedCells.TryGetRefusal(out var refusal);
            return RegistryResult<RegistryOperations.RegisteredStudy>.Rejected(refusal);
        }
        using var compiled = JsonDocument.Parse(compiledStudyJson);
        var root = compiled.RootElement;
        // The same compiled-cell read serves the no-Home check command and registration.
        // Registration then reads the optional compiler provenance and writes the rows.
        string? compilerSha256 = null;
        string? compilerPath = null;
        if (root.TryGetProperty("compiler", out var compiler))
        {
            if (compiler.ValueKind != JsonValueKind.Object
                || !compiler.TryGetProperty("sha256", out var sha) || sha.GetString() is not { Length: 64 } digest
                || !compiler.TryGetProperty("path", out var at) || at.GetString() is not { Length: > 0 } path)
            {
                return Refused("the compiled study's compiler names no sha256 of 64 hexadecimal digits and path");
            }
            (compilerSha256, compilerPath) = (digest, path);
        }
        var registered = RegistryOperations.RegisterStudy(writer, name, revision, compiledStudyJson, sourceId, declared);
        if (compilerSha256 is not null && registered.TryGetValue(out var study))
        {
            var recorded = RegistryOperations.SetStudyCompiler(writer, study.StudyId, compilerSha256, compilerPath!, "register-study");
            if (!recorded.IsOk)
            {
                recorded.TryGetRefusal(out var refusal);
                return Refused($"study {name} is registered as {study.StudyId}, but its compiler was not recorded "
                    + $"({refusal.Detail}); set-study-compiler records it");
            }
        }
        return registered;
    }

    /// <summary>Read the compiled cells with registration's rules, without opening a Home.</summary>
    public static RegistryResult<IReadOnlyList<RegistryOperations.CompiledCell>> CheckCompiledStudy(
        string compiledStudyJson, string device)
    {
        JsonDocument compiled;
        try
        {
            compiled = JsonDocument.Parse(compiledStudyJson);
        }
        catch (JsonException error)
        {
            return RegistryResult<IReadOnlyList<RegistryOperations.CompiledCell>>.Rejected(
                new("refused", "invalid_request", $"compiled study is not JSON: {error.Message}"));
        }
        using (compiled)
        {
            var root = compiled.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return RegistryResult<IReadOnlyList<RegistryOperations.CompiledCell>>.Rejected(
                    new("refused", "invalid_request", "a compiled study is a JSON object"));
            }
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String
                || schema.GetString() != CompiledStudySchema)
            {
                return RegistryResult<IReadOnlyList<RegistryOperations.CompiledCell>>.Rejected(
                    new("refused", "invalid_request", $"schema must be {CompiledStudySchema}"));
            }
            if (!root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
            {
                return RegistryResult<IReadOnlyList<RegistryOperations.CompiledCell>>.Rejected(
                    new("refused", "invalid_request", "a compiled study carries its cells"));
            }

            var dataset = root.TryGetProperty("dataset", out var data)
                && data.TryGetProperty("name", out var datasetName) && datasetName.ValueKind == JsonValueKind.String
                    ? datasetName.GetString()!
                    : "";

            var declared = new List<RegistryOperations.CompiledCell>();
            var ordinal = 0L;
            foreach (var cell in cells.EnumerateArray())
            {
                var read = ReadCell(cell, ordinal, dataset, device);
                if (!read.TryGetValue(out var value))
                {
                    read.TryGetRefusal(out var refusal);
                    return RegistryResult<IReadOnlyList<RegistryOperations.CompiledCell>>.Rejected(refusal);
                }
                declared.Add(value);
                ordinal++;
            }
            return RegistryResult<IReadOnlyList<RegistryOperations.CompiledCell>>.Ok(declared);
        }
    }

    internal static RegistryResult<RegistryOperations.CompiledCell> ReadCell(
        JsonElement cell, long ordinal, string studyDataset, string device)
    {
        if (cell.ValueKind != JsonValueKind.Object)
        {
            return RegistryResult<RegistryOperations.CompiledCell>.Rejected(
                new RegistryRefusal("refused", "invalid_request", $"cell #{ordinal} is not an object"));
        }
        var name = cell.TryGetProperty("cell", out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString()!
            : $"#{ordinal}";
        if (!cell.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            return RegistryResult<RegistryOperations.CompiledCell>.Rejected(
                new RegistryRefusal("refused", "invalid_request", $"cell {name} carries no config"));
        }
        // CHG 2026-09-11, from the retired StudyLaunch admission: C# reads ONLY
        // what it executes. The scientific words of a configuration belong to
        // the Go compiler (tools/fern-compile), which owns the vocabulary and
        // minted the identity. Three facts are read here, each refused by name:
        // the wire the configuration is on, the model type a Site's admitted
        // model list is checked against, and a config_hash of the right shape.
        var configSchema = config.TryGetProperty("schema", out var wire) && wire.ValueKind == JsonValueKind.String
            ? wire.GetString() ?? ""
            : "";
        if (configSchema != TrainingConfigWire)
        {
            return RegistryResult<RegistryOperations.CompiledCell>.Rejected(
                new RegistryRefusal("refused", "invalid_request",
                    $"cell {name}: config.schema must be \"{TrainingConfigWire}\", got \"{configSchema}\""));
        }
        var modelType = config.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString() ?? ""
                : "";
        if (modelType.Length == 0)
        {
            return RegistryResult<RegistryOperations.CompiledCell>.Rejected(
                new RegistryRefusal("refused", "invalid_request",
                    $"cell {name}: config.model.type must be a non-empty string; a Site admits work by model"));
        }
        // The identity the compiler minted travelled as the payload's sha until
        // 2026-09-27 (config-hash-leaves, slice 4): a cell's configuration is its
        // identity, compared field by field, and a config_hash the compiler still
        // writes is not read.

        // CHG 2026-09-14, round 3 of the stage-vocabulary design: the digests
        // the compiler minted for each section of this configuration. A fourth
        // fact read here, and the only one this host does not execute -- it is
        // carried so that "these two cells differ; where?" is a column read
        // rather than a hunt for two files. The SHAPE is checked and the
        // meaning is not: what `model` or `train` names belongs to the
        // vocabulary the compiler owns, and a host that checked it would be
        // re-acquiring exactly what this cut removed.
        // CHG 2026-09-26 (cards-without-hashes; owner: "the card diff section
        // supersedes this"): not read any more. A compiled cell that still carries
        // config_regions registers, and the value is ignored; the table that held
        // them left at schema 18.

        var seed = cell.TryGetProperty("coordinates", out var coordinates)
            && coordinates.TryGetProperty("seed", out var declaredSeed) && declaredSeed.TryGetInt64(out var value)
                ? value
                : 0;
        var dataset = coordinates.ValueKind == JsonValueKind.Object
            && coordinates.TryGetProperty("dataset", out var cellDataset) && cellDataset.ValueKind == JsonValueKind.String
                ? cellDataset.GetString()!
                : studyDataset;

        var requiresCuda = string.Equals(device, "cuda", StringComparison.Ordinal);
        var requirements = JsonSerializer.Serialize(new
        {
            device,
            requires_cuda = requiresCuda,
        });
        var retention = cell.TryGetProperty("unit_outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object
            ? RetentionOf(outputs)
            : """{"unit_results":"retained"}""";

        return RegistryResult<RegistryOperations.CompiledCell>.Ok(new RegistryOperations.CompiledCell(
            ordinal, name, dataset, seed, config.GetRawText(), requirements, retention));
    }

    // CHG 2026-09-26: RegionsOf, which checked the shape of a compiled cell's
    // config_regions before they were stored, left with the table at schema 18.

    /// <summary>
    /// Every declared output is retained. Retention is a DECLARED field on the
    /// cell now, not a label derived from a stage graph's exports: with no
    /// graph there is nothing to derive it from, and a compiled study states
    /// what it wants kept.
    /// </summary>
    static string RetentionOf(JsonElement unitOutputs)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var output in unitOutputs.EnumerateObject())
        {
            kept[output.Name] = "retained";
        }
        return JsonSerializer.Serialize(kept);
    }

    static RegistryResult<RegistryOperations.RegisteredStudy> Refused(string detail) =>
        RegistryResult<RegistryOperations.RegisteredStudy>.Rejected(
            new RegistryRefusal("refused", "invalid_request", detail));

    /// <summary>The wire a resolved training configuration is written on.</summary>
    internal const string TrainingConfigWire = "fern.training.v2";

}
