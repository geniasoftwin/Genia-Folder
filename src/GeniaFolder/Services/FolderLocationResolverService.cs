using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class FolderLocationResolverService
{
    private readonly FolderIdentityService _identity;
    private readonly FolderMarkerService _markers;

    public FolderLocationResolverService(
        FolderIdentityService identity)
        : this(identity, new FolderMarkerService())
    {
    }

    public FolderLocationResolverService(
        FolderIdentityService identity,
        FolderMarkerService markers)
    {
        _identity = identity;
        _markers = markers;
    }

    public string? TryResolve(
        ManagedFolder missingFolder,
        IEnumerable<ManagedFolder> knownFolders)
    {
        // First try exact historical paths. This is the important
        // cross-volume round-trip case: D:\x -> E:\x -> D:\x.
        foreach (var knownPath in EnumerateKnownPaths(missingFolder))
        {
            if (!Directory.Exists(knownPath))
                continue;

            if (CandidateMatches(knownPath, missingFolder))
                return Path.GetFullPath(knownPath);
        }

        var searchRoots = new List<string>();
        var seenRoots = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        AddAncestorRoots(missingFolder.Path);

        foreach (var knownPath in missingFolder.KnownPaths ?? [])
            AddAncestorRoots(knownPath);

        foreach (var known in knownFolders)
        {
            if (known.Id == missingFolder.Id ||
                !Directory.Exists(known.Path))
            {
                continue;
            }

            AddRoot(known.Path);
            AddAncestorRoots(known.Path);
        }

        string? match = null;

        foreach (var root in searchRoots)
        {
            foreach (var candidate in SearchDirectChildren(
                root,
                missingFolder))
            {
                var fullCandidate = Path.GetFullPath(candidate);

                if (match is null)
                {
                    match = fullCandidate;
                    continue;
                }

                if (!string.Equals(
                    match,
                    fullCandidate,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // A copied marker can legitimately exist in two places.
                    // Never guess which copy is the user's intended folder.
                    return null;
                }
            }
        }

        return match;

        void AddAncestorRoots(string path)
        {
            string? ancestor;

            try
            {
                ancestor = Directory.GetParent(path)?.FullName;
            }
            catch
            {
                return;
            }

            for (var depth = 0;
                 depth < 8 && ancestor is not null;
                 depth++)
            {
                AddRoot(ancestor);

                string? parent;
                string? volumeRoot;

                try
                {
                    parent = Directory.GetParent(ancestor)?.FullName;
                    volumeRoot = Path.GetPathRoot(ancestor);
                }
                catch
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(parent))
                    break;

                if (!string.IsNullOrWhiteSpace(volumeRoot) &&
                    string.Equals(
                        Normalize(parent),
                        Normalize(volumeRoot),
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                ancestor = parent;
            }
        }

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

    private IEnumerable<string> SearchDirectChildren(
        string root,
        ManagedFolder missingFolder)
    {
        var matches = new List<string>();

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
                    if ((info.Attributes &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (CandidateMatches(
                        candidate,
                        missingFolder))
                    {
                        matches.Add(candidate);
                    }
                }
                catch
                {
                    // Skip one inaccessible/broken candidate.
                }
            }
        }
        catch
        {
            // Offline/network/access-denied roots are unavailable.
        }

        return matches;
    }

    private bool CandidateMatches(
        string candidate,
        ManagedFolder missingFolder)
    {
        if (_markers.Matches(
            candidate,
            missingFolder.Id))
        {
            return true;
        }

        return
            missingFolder.VolumeSerialNumber != 0 &&
            !string.IsNullOrWhiteSpace(missingFolder.FileId) &&
            _identity.Matches(
                candidate,
                missingFolder.VolumeSerialNumber,
                missingFolder.FileId);
    }

    private static IEnumerable<string> EnumerateKnownPaths(
        ManagedFolder folder)
    {
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var value in
            (folder.KnownPaths ?? [])
                .Append(folder.Path))
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            string full;

            try
            {
                full = Path.GetFullPath(value);
            }
            catch
            {
                continue;
            }

            if (seen.Add(full))
                yield return full;
        }
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
}
