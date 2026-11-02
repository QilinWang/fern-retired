using System.Text.Json;
using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// How many attempts a registered study's cells may spend.
///
/// The retired layer declared this per stage in the compiled Study
/// (`StageRetrySpecV1.MaxTotalAttempts`) and the resident scheduler enforced it.
/// The same declaration is read here from the compiled study document the
/// registration stored — `retry.max_total_attempts` — so a study that knows one
/// of its cells is worth three tries and another worth one can still say so,
/// without a column and without a second place to keep it.
///
/// A study that declares nothing gets <see cref="RegistryOperations.DefaultMaxAttempts"/>.
/// An operator who names `--max-attempts` overrides both, because the person at
/// the machine is allowed to know something the card did not.
/// </summary>
public static class StudyAttemptBudget
{
    /// <summary>The budget this study declared, or the default when it declared none.</summary>
    public static long Of(SqliteConnection connection, long studyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT compiled_study_json FROM studies WHERE id = $id";
        command.Parameters.AddWithValue("$id", studyId);
        if (command.ExecuteScalar() is not string compiled)
        {
            return RegistryOperations.DefaultMaxAttempts;
        }
        try
        {
            using var document = JsonDocument.Parse(compiled);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("retry", out var retry)
                && retry.ValueKind == JsonValueKind.Object
                && retry.TryGetProperty("max_total_attempts", out var declared)
                && declared.TryGetInt64(out var attempts)
                && attempts > 0
                    ? attempts
                    : RegistryOperations.DefaultMaxAttempts;
        }
        catch (JsonException)
        {
            return RegistryOperations.DefaultMaxAttempts;
        }
    }
}
