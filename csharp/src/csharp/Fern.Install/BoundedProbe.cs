using System.Diagnostics;
using System.Text;

namespace Fern.Install;

/// <summary>
/// One bounded child runner for every installer probe (Go: limitedOutput in
/// service.go). Stdout and stderr are read concurrently and each stream is
/// bounded on its own, so a child that floods either one can neither block
/// the parent on a full pipe nor grow its memory past the declared limit. The
/// child is untrusted until its output has been parsed, which is why the
/// limits are explicit arguments rather than defaults.
/// </summary>
public static class BoundedProbe
{
    public abstract record Outcome
    {
        public sealed record Missing(string Path) : Outcome;

        public sealed record NotRegular(string Path) : Outcome;

        public sealed record NotExecutable(string Path) : Outcome;

        public sealed record TimedOut(long TimeoutMs) : Outcome;

        public sealed record UnboundedOutput(long MaxBytes) : Outcome;

        public sealed record Completed(int ExitCode, byte[] Stdout, string Stderr) : Outcome;

        /// <summary>
        /// CHG 2026-09-11 night (slice 2 of the cell port, row 23): the caller
        /// asked for the process to end before it did, and the whole tree was
        /// killed. This is how a cancelled study reaches a cell that is training:
        /// the retired daemon cancelled into its live session, and a foreground
        /// supervisor cancels into the process it owns.
        /// </summary>
        public sealed record Cancelled(long AfterMs) : Outcome;
    }

    public static Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes)
        => Run(program, arguments, timeoutMs, maxBytes, null, null);

    public static Outcome Run(
        string program,
        IReadOnlyList<string> arguments,
        long timeoutMs,
        long maxBytes,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellation = default)
    {
        if (Directory.Exists(program))
        {
            return new Outcome.NotRegular(program);
        }

        if (!File.Exists(program))
        {
            return new Outcome.Missing(program);
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(program);
            if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            {
                return new Outcome.NotExecutable(program);
            }
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = program,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? string.Empty,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                process.StartInfo.Environment[name] = value;
            }
        }

        var clock = Stopwatch.StartNew();
        process.Start();
        process.StandardInput.Close();
        // Dedicated threads, not the thread pool: a blocking pipe read on a
        // starved pool can wait seconds to start, which would read as a
        // child timeout. Each reader stops at its own limit and raises the
        // overflow flag, so a flooding child is killed at once.
        var stdout = new Drain(process.StandardOutput.BaseStream, maxBytes);
        var stderr = new Drain(process.StandardError.BaseStream, maxBytes);
        stdout.Start();
        stderr.Start();

        while (!process.WaitForExit(20))
        {
            if (stdout.Overflow || stderr.Overflow)
            {
                TryKill(process);
                return new Outcome.UnboundedOutput(maxBytes);
            }

            if (clock.ElapsedMilliseconds >= timeoutMs)
            {
                TryKill(process);
                return new Outcome.TimedOut(timeoutMs);
            }

            if (cancellation.IsCancellationRequested)
            {
                TryKill(process);
                return new Outcome.Cancelled(clock.ElapsedMilliseconds);
            }
        }

        // The child has exited; its pipes close with it unless a grandchild
        // inherited them. The readers get the rest of the budget (at least a
        // short grace), then the whole tree is killed and the probe is refused
        // as timed out.
        // CHG 2026-09-26 (review of slice W3): a trainer given no deadline must not leave
        // this reader waiting weeks on a pipe a grandchild keeps open; after the child
        // exits, the readers get at most ten minutes.
        var remaining = Math.Min(Math.Max(200, timeoutMs - clock.ElapsedMilliseconds), (long)TimeSpan.FromMinutes(10).TotalMilliseconds);
        if (!stdout.Join(remaining) || !stderr.Join(remaining))
        {
            TryKill(process);
            return new Outcome.TimedOut(timeoutMs);
        }

        if (stdout.Overflow || stderr.Overflow)
        {
            return new Outcome.UnboundedOutput(maxBytes);
        }

        return new Outcome.Completed(process.ExitCode, stdout.Bytes, Encoding.UTF8.GetString(stderr.Bytes));
    }

    /// <summary>
    /// Keeps at most <c>limit</c> bytes; the first byte past the limit sets
    /// <see cref="Overflow"/> and the reader stops, which is the durable fact
    /// that more arrived than the probe allows.
    /// </summary>
    sealed class Drain
    {
        readonly Stream stream;
        readonly long limit;
        readonly MemoryStream kept = new();
        readonly Thread thread;
        volatile bool overflow;

        public Drain(Stream stream, long limit)
        {
            this.stream = stream;
            this.limit = limit;
            thread = new Thread(Read) { IsBackground = true, Name = "fern-bounded-probe-reader" };
        }

        public bool Overflow => overflow;

        public byte[] Bytes => kept.ToArray();

        public void Start() => thread.Start();

        public bool Join(long milliseconds) => thread.Join((int)Math.Clamp(milliseconds, 1, int.MaxValue));

        void Read()
        {
            var chunk = new byte[4096];
            try
            {
                while (true)
                {
                    var n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0)
                    {
                        return;
                    }

                    var room = limit - kept.Length;
                    if (room > 0)
                    {
                        kept.Write(chunk, 0, (int)Math.Min(room, n));
                    }

                    if (n > room)
                    {
                        overflow = true;
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }
}
