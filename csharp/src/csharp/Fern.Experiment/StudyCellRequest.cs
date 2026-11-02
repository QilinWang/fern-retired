using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fern.Experiment;

/// <summary>
/// The study chain's formal shape: the one input a registered study cell
/// carries, which card, which cell and on which device, written once by the
/// launcher from the compiled study and read by the Site's handler
/// <c>fern.study.run_cell</c>. Its bytes are canonical JSON, so the declared
/// input digest and the Site's check agree byte for byte.
/// </summary>
/// <remarks>
/// Its own file because it is a contract two sides read (the launcher writes
/// it, the Python runtime under fern_stage_runtime/cli.py reads it).
/// </remarks>
public static class StudyCellRequest
{
    public const string Schema = "fern.study_cell_request.v1";
    public const string CompiledStudySchema = "fern.compiled-study.v1";

    /// <summary>
    /// The input port this document arrives on. The handler reads it by this
    /// name (<c>required_input_path(request, workspace, "study_cell_request")</c>),
    /// so the launcher that declares the port and the runtime that opens it are
    /// naming one thing.
    /// </summary>
    public const string Port = "study_cell_request";

    /// <summary>
    /// A cell name is ONE path segment. `collect-study` writes each unit under
    /// `&lt;--into&gt;/&lt;cell name&gt;/&lt;file&gt;` in overwrite mode, so a name carrying a
    /// path separator or `..` would place a unit outside the folder the
    /// operator named. Both ends refuse it: the card refuses at compile in
    /// fern_train.study, and a compiled study reaching this admission refuses here.
    /// </summary>
    static readonly Regex CellName = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    /// <summary>The refusal text for a cell name that is not one path segment, or null when it is.</summary>
    public static string? CellNameRefusal(string cellName) =>
        cellName is "." or ".." || !CellName.IsMatch(cellName)
            ? $"cell name {cellName} is not one path segment: letters, digits, underscore, dot or hyphen, and not \".\" or \"..\", because collect-study writes the cell's units under a folder of that name"
            : null;

    /// <param name="checkpointStore">
    /// The store root a cell that evaluates a checkpoint BY NAME resolves that name in — the
    /// <c>store/</c> directory the control-plane installer creates beside the Registry Home. The
    /// store's own receipt says which bytes the name is; the card only says the name. Empty when
    /// the caller has no store to name, and the field is then absent, which is the refusal the
    /// Python worker already gives by name.
    /// </param>
    /// <param name="resultsRoot">
    /// The selected results directory on the machine that will run this cell, when that machine
    /// can reach it. The runtime saves the cell's results document into the science store under
    /// that root the moment the cell ends, and reports back where it went and the digest of what
    /// it saved. Empty when the machine cannot reach the results tree — a remote Site — and the
    /// field is then absent, which is how the runtime knows to hand the document back instead.
    /// C# never names a database: it names the ROOT, and Python's `science_database_for` decides
    /// which file under it a result belongs in.
    /// </param>
    public static TextResult<byte[]> Assemble(
        JsonElement compiledStudy, JsonElement cell, string device, string checkpointStore = "",
        string resultsRoot = "")
    {
        if (!compiledStudy.TryGetProperty("schema", out var schema) || schema.GetString() != CompiledStudySchema)
        {
            return TextResult<byte[]>.Refused($"compiled study schema must be {CompiledStudySchema}");
        }
        if (!compiledStudy.TryGetProperty("resolution", out var resolution)
            || !resolution.TryGetProperty("resolved", out var resolved)
            || !resolved.TryGetProperty("specification", out var specification)
            || specification.ValueKind != JsonValueKind.String
            || !resolved.TryGetProperty("manifest", out var card)
            || card.ValueKind != JsonValueKind.Object)
        {
            return TextResult<byte[]>.Refused("compiled study must carry resolution.resolved.specification and the card as its manifest");
        }
        var specificationText = specification.GetString() ?? "";
        // CHG 2026-09-26 (owner: "get rid of 256 entirely"): the specification
        // names the study, "study:<its name>"; the card itself is the manifest
        // beside it. A study compiled before carries "study:sha256:<64 hex>"
        // (a digest of the card's bytes) and is read as it was written.
        const string former = "study:sha256:";
        var named = compiledStudy.TryGetProperty("study", out var studyName) && studyName.ValueKind == JsonValueKind.String
            ? "study:" + studyName.GetString() : null;
        var formerForm = specificationText.StartsWith(former, StringComparison.Ordinal) && specificationText.Length == former.Length + 64;
        if (specificationText != named && !formerForm)
        {
            return TextResult<byte[]>.Refused(
                $"compiled study specification must be study:<its name>, or {former}<64 hex> for a study compiled before 2026-09-26; got {specificationText}");
        }
        if (!cell.TryGetProperty("cell", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
        {
            return TextResult<byte[]>.Refused("compiled study cell must name its cell");
        }
        if (CellNameRefusal(name.GetString()!) is { } nameRefusal)
        {
            return TextResult<byte[]>.Refused(nameRefusal);
        }
        // CHG 2026-09-27 (config-hash-leaves, slice 3): the compiler mints no config_hash;
        // the cell carries its configuration, which the worker compares whole.
        if (!cell.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            return TextResult<byte[]>.Refused($"compiled study cell {name.GetString()} must carry its config");
        }
        if (string.IsNullOrWhiteSpace(device))
        {
            return TextResult<byte[]>.Refused("a study cell request names the device it trains on");
        }
        var datasetInputs = compiledStudy.TryGetProperty("dataset", out var dataset)
            && dataset.TryGetProperty("inputs", out var inputs)
            && inputs.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(inputs.GetRawText())
                : new JsonObject();
        var request = new JsonObject
        {
            ["schema"] = Schema,
            ["study"] = compiledStudy.TryGetProperty("study", out var study) ? study.GetString() : null,
            ["cell"] = name.GetString(),
            // CHG 2026-09-10: the seed is the CELL's, not the study's. A card
            // declaring `seeds = [42, 43]` compiles one cell per line per seed
            // and each carries its own `coordinates.seed`; reading the study
            // head instead issued the first seed to every cell and labelled the
            // rest with seeds they never ran. The head remains the fallback for
            // a compiled study old enough to carry no cell coordinates.
            ["seed"] = CellSeed(compiledStudy, cell),
            ["device"] = device,
            ["card"] = JsonNode.Parse(card.GetRawText()),
            ["config"] = JsonNode.Parse(config.GetRawText()),
            ["dataset_inputs"] = datasetInputs,
            // CHG 2026-09-26 (owner: "compile远端其实只是检查，本地compile卡片即可";
            // docs/programs/2026-09-26-source-roots-stop-being-full-copies, slice 3b):
            // the study as it was compiled here and registered, so the worker
            // checks the cell against it instead of compiling the card again,
            // which needed the same fern-compile on every machine.
            ["compiled_study"] = JsonNode.Parse(compiledStudy.GetRawText()),
        };
        if (!string.IsNullOrEmpty(checkpointStore))
        {
            request["checkpoint_store"] = checkpointStore;
        }
        if (!string.IsNullOrEmpty(resultsRoot))
        {
            request["results_root"] = resultsRoot;
        }
        if (compiledStudy.TryGetProperty("promotion_evidence", out var promotionEvidence)
            && promotionEvidence.ValueKind == JsonValueKind.Object)
        {
            request["compile_evidence"] = JsonNode.Parse(promotionEvidence.GetRawText());
        }
        using var document = JsonDocument.Parse(request.ToJsonString());
        return TextResult<byte[]>.Ok(CanonicalJson.Bytes(document.RootElement));
    }

    /// <summary>The seed this cell actually trains at: its own coordinate, or the study's when a cell carries none.</summary>
    static long CellSeed(JsonElement compiledStudy, JsonElement cell)
    {
        if (cell.TryGetProperty("coordinates", out var coordinates)
            && coordinates.ValueKind == JsonValueKind.Object
            && coordinates.TryGetProperty("seed", out var cellSeed)
            && cellSeed.ValueKind == JsonValueKind.Number)
        {
            return cellSeed.GetInt64();
        }
        return compiledStudy.TryGetProperty("seed", out var studySeed) && studySeed.ValueKind == JsonValueKind.Number
            ? studySeed.GetInt64()
            : 0;
    }

}
