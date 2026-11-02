using System.Text;

namespace Fern.Install;

/// <summary>
/// Pass 6 bounded identity reader. Timeout 5s, 64 KiB. Caller JSON of the
/// probe output is strict per Pass 2 addendum
/// reference:7930679b4dc5ecbdeff33f81d7a6c99cd92024da0d1c57d0bc708e169e34c7b3.
/// </summary>
public static class IdentityProbe
{
    public const long TimeoutMs = 5000;
    public const long MaxOutputBytes = 65536;

    public abstract record Outcome
    {
        public sealed record Accepted(InstallationIdentity Identity) : Outcome;

        public sealed record TimedOut : Outcome;

        public sealed record UnboundedOutput : Outcome;

        public sealed record ParseFailed(InstallationIdentityParse Parse) : Outcome;

        public sealed record Missing(string Path) : Outcome;

        public sealed record NotRegular(string Path) : Outcome;

        public sealed record NotExecutable(string Path) : Outcome;

        public sealed record Failed(int ExitCode, string Stderr) : Outcome;
    }

    public static Outcome Run(
        string program,
        string expectedRole,
        long timeoutMs = TimeoutMs,
        long maxBytes = MaxOutputBytes,
        params string[] arguments)
    {
        // CHG 2026-08-27: the process bounding moved to BoundedProbe so the
        // worker, Card writer and Stage runner probes share one reader; the
        // outcome contract here is unchanged except that a non-zero exit is
        // now a Failed outcome, as it is in Go, instead of being parsed.
        var outcome = BoundedProbe.Run(program, arguments, timeoutMs, maxBytes);
        switch (outcome)
        {
            case BoundedProbe.Outcome.Missing missing:
                return new Outcome.Missing(missing.Path);
            case BoundedProbe.Outcome.NotRegular notRegular:
                return new Outcome.NotRegular(notRegular.Path);
            case BoundedProbe.Outcome.NotExecutable notExecutable:
                return new Outcome.NotExecutable(notExecutable.Path);
            case BoundedProbe.Outcome.TimedOut:
                return new Outcome.TimedOut();
            case BoundedProbe.Outcome.UnboundedOutput:
                return new Outcome.UnboundedOutput();
        }

        var completed = (BoundedProbe.Outcome.Completed)outcome;
        if (completed.ExitCode != 0)
        {
            return new Outcome.Failed(completed.ExitCode, completed.Stderr);
        }

        var text = Encoding.UTF8.GetString(completed.Stdout);
        var parsed = InstallationIdentities.Parse(text.Trim(), expectedRole);
        return parsed is InstallationIdentityParse.Accepted accepted
            ? new Outcome.Accepted(accepted.Identity)
            : new Outcome.ParseFailed(parsed);
    }
}
