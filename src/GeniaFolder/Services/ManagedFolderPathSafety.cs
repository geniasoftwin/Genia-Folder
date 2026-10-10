using GeniaFolder.Models;

namespace GeniaFolder.Services;

public static class ManagedFolderPathSafety
{
    public static string? FindOverlappingManagedPath(
        ManagedFolder candidate,
        IEnumerable<ManagedFolder> managedFolders)
    {
        var candidatePath = Normalize(candidate.Path);

        foreach (var other in managedFolders)
        {
            if (other.Id == candidate.Id)
                continue;

            var otherPath = Normalize(other.Path);
            if (Overlaps(candidatePath, otherPath))
                return other.Path;
        }

        return null;
    }

    public static bool Overlaps(string first, string second)
    {
        first = Normalize(first);
        second = Normalize(second);

        return string.Equals(
                   first, second, StringComparison.OrdinalIgnoreCase)
               || first.StartsWith(
                   second + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
               || second.StartsWith(
                   first + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
}
