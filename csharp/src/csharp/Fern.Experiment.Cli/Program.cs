using Fern.Experiment;

// fern-experiment: the process shell around the typed report operations the
// study route and the Python host door call.
//
//   fern-experiment <build-artifact-dir|show-lane-paths>
//     stdin   one request JSON document
//     stdout  the receipt JSON, then one newline -- only when every member lowered
//     stderr  "fern-experiment: <field>: <explanation>" on refusal, exit 2
//
// Everything typed lives in csharp/Fern.Experiment; this file reads bytes,
// calls Execute, and writes bytes. The usage line above is the two operations
// this door actually serves; it advertised three retired ones until 2026-09-11,
// which is the kind of stale sentence an operator reads as a promise. The real
// usage string is built from the dictionary below, so the two can only disagree
// in a comment.
//
// WHO CALLS THESE TWO. No production code in this repository does: Python owns
// both derivations (`python/fern_analysis/lanes.py` for lane paths, and the
// artifact path beside it), and the callers are two Python tests that check the
// two implementations still agree — tests/test_artifact_path_golden.py and
// tests/test_paper_lanes.py. That is a real purpose and also the whole of it: if
// those checks are ever dropped, this binary has no reason to exist and Python
// is the single owner of both derivations. CHG 2026-09-04: eight operations nothing
// called left this door (apply-config-overlays, admit-recipe-extensions,
// resolve-dataset-source-capabilities, check-recipe-handler-compatibility,
// resolve-experiment-warnings, publish-implementation-handler-capabilities,
// resolve-recipe-hash, select-recipes); the library functions fern-run and the
// resolver call in-process stayed where they were. CHG 2026-09-04, the recipe
// route retired: list-recipe-run-targets, list-recipes, list-presets and
// catalog left with the catalog they read, and lower-cell-batch, pair-ablation
// and lower-pair-batch left with the run-experiment and run-ablation verbs that
// were their only callers (retired/csharp/recipe-route-2026-09-04).

// CHG 2026-09-05: build-config and its unchecked overlay library are retired.
// Configurations arrive complete from the card compiler; identity and plan-run
// remain readers of those documents, not a second source of defaults.
return ExperimentCli.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);

static class ExperimentCli
{
    static readonly IReadOnlyDictionary<string, Func<byte[], TextResult<byte[]>>> Reports =
        new Dictionary<string, Func<byte[], TextResult<byte[]>>>(StringComparer.Ordinal)
        {
            [ArtifactPathCli.Operation] = ArtifactPathCli.Execute,
            [LanePathsCli.Operation] = LanePathsCli.Execute,
            // CHG 2026-09-11, the vocabulary cut: show-config-identity and
            // plan-run both began by admitting a resolved configuration through
            // the generated C# reader, and that reader is retired -- the Go
            // compiler owns the vocabulary and mints the identity, and Python
            // asks it directly (fern-compile identity). plan-run is under
            // retired/csharp/plan-run-2026-09-11.
        };

    public static int Run(string[] args, Stream stdin, Stream stdout, TextWriter stderr)
    {
        var known = Reports.Keys.ToList();
        if (args.Length != 1 || !known.Contains(args[0], StringComparer.Ordinal))
        {
            stderr.WriteLine($"usage: fern-experiment <{string.Join("|", known)}>");
            return 2;
        }

        using var request = new MemoryStream();
        stdin.CopyTo(request);
        if (Reports.TryGetValue(args[0], out var report))
        {
            var produced = report(request.ToArray());
            if (!produced.IsOk)
            {
                stderr.WriteLine($"fern-experiment: {produced.Refusal}");
                return 2;
            }

            stdout.Write(produced.Value, 0, produced.Value.Length);
            stdout.Flush();
            return 0;
        }

        stderr.WriteLine($"fern-experiment: no operation named {args[0]}");
        return 2;
    }
}
