using System.Diagnostics;

namespace GeniaFolder.Services;

public static class ShellService
{
    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(path);

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
