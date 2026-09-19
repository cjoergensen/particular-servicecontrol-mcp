namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>A temporary folder that this process and the ServiceControl containers share through a bind mount.</summary>
static class SharedFolder
{
    /// <summary>Creates the folder, world-writable so a container user that differs from this one can still use it, and registers its cleanup.</summary>
    public static string Create(List<IAsyncDisposable> owned)
    {
        var path = Path.Combine(Path.GetTempPath(), "scmcp-learning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                       UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        owned.Add(new FolderCleanup(path));
        return path;
    }

    /// <summary>Removes a temporary folder when the platform is torn down.</summary>
    sealed class FolderCleanup(string path) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a temp folder left behind is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Files created by a container may not be deletable by this user; leave them.
            }

            return ValueTask.CompletedTask;
        }
    }
}
