using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

/// <summary>F2: where the Worker finds the secret it presents to the App.</summary>
public class WorkerSecretProviderTests
{
    private static string? NoEnvironment(string name) => null;

    private static string NewTempFile(string? content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gm-workersecret-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "worker.secret");
        if (content is not null)
            File.WriteAllText(path, content);
        return path;
    }

    private static void Cleanup(string filePath) => Directory.Delete(Path.GetDirectoryName(filePath)!, recursive: true);

    [Fact]
    public void Missing_file_and_no_environment_variable_means_no_secret()
    {
        var path = NewTempFile(content: null);
        try
        {
            Assert.Null(WorkerSecretProvider.Resolve(NoEnvironment, path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Secret_is_read_from_the_file_and_trimmed()
    {
        var path = NewTempFile("abc123_-XYZ\r\n");
        try
        {
            Assert.Equal("abc123_-XYZ", WorkerSecretProvider.Resolve(NoEnvironment, path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Environment_variable_wins_over_the_file()
    {
        var path = NewTempFile("from-file");
        try
        {
            var secret = WorkerSecretProvider.Resolve(
                name => name == WorkerSecretProvider.EnvironmentVariableName ? " from-env " : null,
                path);

            Assert.Equal("from-env", secret);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Blank_file_means_no_secret()
    {
        var path = NewTempFile("  \r\n");
        try
        {
            Assert.Null(WorkerSecretProvider.Resolve(NoEnvironment, path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Unreadable_file_means_no_secret_instead_of_throwing()
    {
        // A directory at the file path cannot be read as a file.
        var dir = Path.Combine(Path.GetTempPath(), "gm-workersecret-worker-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "worker.secret");
        Directory.CreateDirectory(path);
        try
        {
            Assert.Null(WorkerSecretProvider.Resolve(NoEnvironment, path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Default_file_path_is_under_the_machine_wide_graymoon_folder()
    {
        Assert.EndsWith(Path.Combine("GrayMoon", "worker.secret"), WorkerSecretProvider.DefaultFilePath);
    }
}
