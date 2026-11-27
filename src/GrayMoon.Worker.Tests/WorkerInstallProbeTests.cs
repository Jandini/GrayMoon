using System.Diagnostics;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class WorkerInstallProbeTests
{
    [Fact]
    public void An_intact_install_reports_no_damage()
    {
        Assert.Null(new WorkerInstallProbe().Check());
    }

    /// <summary>
    /// The probe runs in a child process that has the managed assemblies but not the native git2 library, which is
    /// what a torn install looks like. In this process the library is already loaded by other tests, so the check
    /// cannot be made here. Before the fix the probe read only the version (assembly attributes) and answered
    /// "no damage".
    /// </summary>
    [Fact]
    public void A_missing_native_library_is_reported_as_damage()
    {
        var folder = TestDirectory.Create("install-probe");
        try
        {
            // Top-level files only: the native libraries live under runtimes\, and without the deps file the host
            // resolves the managed assemblies from this folder alone.
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "GrayMoon.Worker.Tests.runtimeconfig.json"),
                Path.Combine(folder, "GrayMoon.Worker.Tests.runtimeconfig.json"));

            var damaged = RunProbe(folder);

            Assert.StartsWith(TestProcessEntryPoint.ProbeResultPrefix, damaged);
            Assert.NotEqual(TestProcessEntryPoint.ProbeResultPrefix + TestProcessEntryPoint.Intact, damaged);
            Assert.Contains("git2", damaged);

            // The same child from the real output folder, where the library is present: shows that the child run
            // itself works and that only the missing library made the difference.
            Assert.Equal(TestProcessEntryPoint.ProbeResultPrefix + TestProcessEntryPoint.Intact, RunProbe(AppContext.BaseDirectory));
        }
        finally
        {
            TestDirectory.Delete(folder);
        }
    }

    private static string RunProbe(string folder)
    {
        var host = Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? path
            : "dotnet";
        var startInfo = new ProcessStartInfo(host)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(folder, "GrayMoon.Worker.Tests.dll"));
        startInfo.ArgumentList.Add(TestProcessEntryPoint.InstallProbeArgument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The child process could not be started.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var result = output.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(line => line.StartsWith(TestProcessEntryPoint.ProbeResultPrefix, StringComparison.Ordinal));
        return result ?? throw new InvalidOperationException($"The child process printed no probe result (exit {process.ExitCode}). Output: {output} Error: {error.Result}");
    }
}
