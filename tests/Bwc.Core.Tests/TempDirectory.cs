namespace Bwc.Core.Tests;

/// <summary>Creates a unique folder under the process temp directory and removes it on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "bwc-core-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        // Elevated-mode tests hold their data folders open; release them so the folder can be removed.
        SafeDataFolder.ReleaseAll();
        if (!Directory.Exists(Path))
        {
            return;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 2 && (ex is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(100);
            }
        }
    }
}
