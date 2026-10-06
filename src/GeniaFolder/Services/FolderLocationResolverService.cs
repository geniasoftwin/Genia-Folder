using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class FolderLocationResolverService
{
    private readonly FolderIdentityService _identity;

    public FolderLocationResolverService(
        FolderIdentityService identity)
    {
        _identity = identity;
    }

    public string? TryResolve(
        ManagedFolder missingFolder,
        IEnumerable<ManagedFolder> knownFolders)
    {
        if (missingFolder.VolumeSerialNumber == 0 ||
            string.IsNullOrWhiteSpace(missingFolder.FileId))
        {
            return null;
        }

        var searchRoots = new List<string>();
        var seenRoots = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var oldParent = Directory.GetParent(
            missingFolder.Path)?.FullName;

        AddRoot(oldParent);

        foreach (var known in knownFolders)
        {
            if (known.Id == missingFolder.Id ||
                !Directory.Exists(known.Path))
            {
                continue;
            }

            AddRoot(known.Path);
        }

        foreach (var root in searchRoots)
        {
            var resolved = SearchDirectChildren(
                root,
                missingFolder);

            if (resolved is not null)
                return resolved;
        }

        return null;

        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) ||
                !Directory.Exists(root))
            {
                return;
            }

            string fullRoot;

            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch
            {
                return;
            }

            if (seenRoots.Add(fullRoot))
                searchRoots.Add(fullRoot);
        }
    }

    private string? SearchDirectChildren(
        string root,
        ManagedFolder missingFolder)
    {
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(
                root,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new DirectoryInfo(candidate);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    if (_identity.Matches(
                        candidate,
                        missingFolder.VolumeSerialNumber,
                        missingFolder.FileId))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    // Skip one inaccessible/broken candidate and continue.
                }
            }
        }
        catch
        {
            // Offline/network/access-denied roots are simply unavailable.
        }

        return null;
    }
}
