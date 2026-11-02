using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class RegistryAccessTests
{
    [Fact]
    public void OrdinaryReadersAndWritersCanStillCoexist()
    {
        using var fixture = new Fixture();
        using var reader = RegistryHome.OpenReader(fixture.Path);
        using var writer = RegistryHome.OpenWriter(fixture.Path);
        using var secondReader = RegistryHome.OpenReader(fixture.Path);
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM registry_schema";
        Assert.True(Convert.ToInt64(command.ExecuteScalar()) > 0);
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(fixture.Path));
    }

    [Fact]
    public void FailedSqliteValidationReleasesAddressAccess()
    {
        using var fixture = new Fixture();
        var invalid = System.IO.Path.Combine(fixture.Directory, "invalid.db");
        File.WriteAllBytes(invalid, []);
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(invalid));
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenWriter(invalid));
        using var maintenance = RegistryAccess.BeginMaintenance(invalid);
        maintenance.ResumeAccess();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenConnectionPreventsMaintenanceUntilDisposed(bool writer)
    {
        using var fixture = new Fixture();
        using (IDisposable connection = writer ? RegistryHome.OpenWriter(fixture.Path) : RegistryHome.OpenReader(fixture.Path))
            Assert.Throws<RegistryFault>(() => RegistryAccess.BeginMaintenance(fixture.Path));
        using var maintenance = RegistryAccess.BeginMaintenance(fixture.Path);
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(fixture.Path));
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenWriter(fixture.Path));
        Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(fixture.Path));
        maintenance.ResumeAccess();
    }

    [Fact]
    public void AHeldDatabaseEqualsItsCopyAndNothingElse()
    {
        using var fixture = new Fixture();
        using var maintenance = RegistryAccess.BeginMaintenance(fixture.Path);
        var copy = System.IO.Path.Combine(fixture.Directory, "copy.db");
        using (var output = File.Create(copy)) maintenance.CopyClosedDatabase(output);
        Assert.True(maintenance.ClosedDatabaseEquals(copy));
        var bytes = File.ReadAllBytes(copy);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(copy, bytes);
        Assert.False(maintenance.ClosedDatabaseEquals(copy));
        File.WriteAllBytes(copy, bytes[..^1]);
        Assert.False(maintenance.ClosedDatabaseEquals(copy));
        maintenance.ResumeAccess();
    }

    [Fact]
    public void OrderlyMaintenanceAutomaticallyResumesUnchangedSource()
    {
        using var fixture = new Fixture();
        RegistryAccess.BeginMaintenance(fixture.Path).Dispose();
        using var reader = RegistryHome.OpenReader(fixture.Path);
    }

    [Fact]
    public void ExceptionBeforeRelocationAutomaticallyResumesOriginalSource()
    {
        using var fixture = new Fixture();
        Assert.Throws<IOException>((Action)(() =>
        {
            using var maintenance = RegistryAccess.BeginMaintenance(fixture.Path);
            throw new IOException("fixture archive destination unavailable before relocation");
        }));
        using var writer = RegistryHome.OpenWriter(fixture.Path);
    }

    [Fact]
    public void ReplacedSourceIsNotAutomaticallyResumed()
    {
        using var fixture = new Fixture();
        File.Copy(fixture.Path, fixture.Path + ".replacement");
        using (var maintenance = RegistryAccess.BeginMaintenance(fixture.Path))
        {
            File.Move(fixture.Path, fixture.Path + ".original");
            File.Move(fixture.Path + ".replacement", fixture.Path);
        }
        Assert.Contains("maintenance", Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(fixture.Path)).Message);
    }

    [Fact]
    public void ReadOnlyCopyNeedsNoAccessFileOrDirectoryWrites()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var folder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(fixture.Directory, "archive")).FullName;
        var copy = System.IO.Path.Combine(folder, "copy.db");
        File.Copy(fixture.Path, copy);
        File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var reader = RegistryHome.OpenReader(copy);
            using var command = reader.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM registry_schema";
            Assert.True(Convert.ToInt64(command.ExecuteScalar()) > 0);
            Assert.Equal([copy], System.IO.Directory.GetFiles(folder));
            var process = ProductionHome.RunFernRegistry(
                ["work-facts", "--registry", copy, "--id", "frozen-copy", "--json"], "", 10000);
            Assert.Equal(0, process.Exit);
            Assert.Equal([copy], System.IO.Directory.GetFiles(folder));
            Assert.Contains("Frozen", Assert.Throws<RegistryFault>(() => RegistryHome.OpenWriter(copy)).Message);
            Assert.True(Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(copy)).Receipt.Replayed);
            Assert.Equal([copy], System.IO.Directory.GetFiles(folder));
        }
        finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void ReaderWithoutAccessFileStillBlocksMaintenance()
    {
        using var fixture = new Fixture();
        var copy = System.IO.Path.Combine(fixture.Directory, "copy.db");
        File.Copy(fixture.Path, copy);
        using (var reader = RegistryHome.OpenReader(copy))
        {
            Assert.False(File.Exists(copy + ".access"));
            Assert.Throws<RegistryFault>(() => RegistryAccess.BeginMaintenance(copy));
        }
        using var maintenance = RegistryAccess.BeginMaintenance(copy);
    }

    [Fact]
    public void FrozenCopyWithCommittedWalIsNotSilentlyReadWithoutItsJournal()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var folder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(fixture.Directory, "archive")).FullName;
        var copy = System.IO.Path.Combine(folder, "copy.db");
        using (var writer = RegistryHome.OpenWriter(fixture.Path))
        {
            using var command = writer.Connection.CreateCommand();
            // CHG 2026-09-26: the marker is application_id, not user_version,
            // which is the schema version the Registry now reads before objects.
            command.CommandText = "PRAGMA application_id = 37";
            command.ExecuteNonQuery();
            Assert.True(new FileInfo(fixture.Path + "-wal").Length > 0);
            using var reader = RegistryHome.OpenReader(fixture.Path);
            using var query = reader.Connection.CreateCommand();
            query.CommandText = "PRAGMA application_id";
            Assert.Equal(37L, query.ExecuteScalar());
            File.Copy(fixture.Path, copy);
            File.Copy(fixture.Path + "-wal", copy + "-wal");
        }
        File.SetUnixFileMode(copy, UnixFileMode.UserRead);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var fault = Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(copy));
            Assert.Contains("journal", fault.Message);
            Assert.True(File.Exists(copy + "-wal"));
            Assert.False(File.Exists(copy + ".access"));
        }
        finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void RetiredAddressCannotBeOpenedOrRecreated()
    {
        using var fixture = new Fixture();
        using (var maintenance = RegistryAccess.BeginMaintenance(fixture.Path))
        {
            maintenance.RetireAddress();
            Assert.Throws<RegistryFault>(() => maintenance.ResumeAccess());
        }
        Assert.Contains("retired", Assert.Throws<RegistryFault>(() => RegistryHome.OpenWriter(fixture.Path)).Message);
        File.Delete(fixture.Path); // fixture only: model the source being absent after relocation.
        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(fixture.Path));
        Assert.Contains("retired", refusal.Detail);
        Assert.False(File.Exists(fixture.Path));
        Assert.Throws<RegistryFault>(() => RegistryAccess.BeginMaintenance(fixture.Path));
        var process = ProductionHome.RunFernRegistry(["initialize-registry", "--registry", fixture.Path], "", 10000);
        Assert.NotEqual(0, process.Exit);
        Assert.Contains("retired", process.Stdout + process.Stderr);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public void PublicProcessCannotReadDuringMaintenance()
    {
        using var fixture = new Fixture();
        string[] args = ["work-facts", "--registry", fixture.Path, "--id", "maintenance-test", "--json"];
        using (var maintenance = RegistryAccess.BeginMaintenance(fixture.Path))
        {
            var result = ProductionHome.RunFernRegistry(args, "", 10000);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("Registry access", result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
            maintenance.ResumeAccess();
        }
        Assert.Equal(0, ProductionHome.RunFernRegistry(args, "", 10000).Exit);
    }

    [Fact]
    public void CorruptAccessStateRefusesBeforeDatabaseCreation()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Path);
        File.WriteAllText(fixture.Path + ".access", "broken");
        Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(fixture.Path));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public void MissingDatabaseOpenDoesNotCreateAnything()
    {
        using var directory = new Fixture();
        var missing = System.IO.Path.Combine(directory.Directory, "missing.db");
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(missing));
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenWriter(missing));
        Assert.False(File.Exists(missing));
        Assert.False(File.Exists(missing + ".access"));
    }

    [Fact]
    public void SymbolicPathCannotSelectAnotherAccessFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var alias = System.IO.Path.Combine(fixture.Directory, "alias.db");
        File.CreateSymbolicLink(alias, fixture.Path);
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(alias));
        Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(alias));
        Assert.False(File.Exists(alias + ".access"));
    }

    [Fact]
    public void HardLinkedDatabaseCannotSelectAnotherAccessFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var alias = System.IO.Path.Combine(fixture.Directory, "hardlink.db");
        Assert.Equal(0, Link(fixture.Path, alias));
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(alias));
        Assert.Throws<RegistryFault>(() => RegistryHome.OpenWriter(fixture.Path));
        Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(alias));
    }

    [Theory]
    [InlineData("writer")]
    [InlineData("maintenance")]
    public void ProcessDeathReleasesLockButNotMaintenanceState(string mode)
    {
        using var fixture = new Fixture();
        var ready = System.IO.Path.Combine(fixture.Directory, "child-ready");
        var start = new ProcessStartInfo(System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[] { "vstest", typeof(RegistryAccessTests).Assembly.Location,
                     "--TestCaseFilter:FullyQualifiedName=Fern.Registry.Tests.RegistryAccessTests.AccessHolderChild" })
            start.ArgumentList.Add(arg);
        start.Environment["FERN_ACCESS_TEST_DATABASE"] = fixture.Path;
        start.Environment["FERN_ACCESS_TEST_MODE"] = mode;
        start.Environment["FERN_ACCESS_TEST_READY"] = ready;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        Process? holder = null;
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(ready) && !child.HasExited && elapsed.ElapsedMilliseconds < 20000)
                Thread.Sleep(20);
            Assert.True(File.Exists(ready), "isolated access holder did not become ready");
            holder = Process.GetProcessById(int.Parse(File.ReadAllText(ready)));
            Assert.Throws<RegistryFault>(() => RegistryAccess.BeginMaintenance(fixture.Path));
        }
        finally
        {
            // Wait for the actual testhost owning the descriptor, not just
            // vstest's parent. Kill(entireProcessTree) does not wait for descendants.
            if (holder is not null)
            {
                if (!holder.HasExited) holder.Kill();
                Assert.True(holder.WaitForExit(10000));
                holder.Dispose();
            }
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(10000));
        }
        if (mode == "maintenance")
        {
            Assert.Contains("maintenance", Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(fixture.Path)).Message);
            // Acquiring an already interrupted operation is not evidence that
            // it had no effect: merely disposing recovery cannot reopen it.
            RegistryAccess.BeginMaintenance(fixture.Path).Dispose();
            Assert.Contains("maintenance", Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(fixture.Path)).Message);
        }
        using var recovered = RegistryAccess.BeginMaintenance(fixture.Path);
        recovered.ResumeAccess();
    }

    [Fact]
    public void AccessHolderChild()
    {
        var path = Environment.GetEnvironmentVariable("FERN_ACCESS_TEST_DATABASE");
        if (path is null) return;
        using IDisposable held = Environment.GetEnvironmentVariable("FERN_ACCESS_TEST_MODE") == "writer"
            ? RegistryHome.OpenWriter(path) : RegistryAccess.BeginMaintenance(path);
        var ready = Environment.GetEnvironmentVariable("FERN_ACCESS_TEST_READY")!;
        File.WriteAllText(ready + ".tmp", Environment.ProcessId.ToString());
        File.Move(ready + ".tmp", ready);
        Thread.Sleep(Timeout.Infinite);
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    static extern int Link(string existing, string alias);

    sealed class Fixture : IDisposable
    {
        public string Path { get; } = ProductionHome.CreateRegistry();
        public string Directory => System.IO.Path.GetDirectoryName(Path)!;
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
