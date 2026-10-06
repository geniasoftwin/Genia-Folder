using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class FolderAppearanceService
{
    private const uint SHCNE_ATTRIBUTES = 0x00000800;
    private const uint SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNE_UPDATEITEM = 0x00002000;

    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSH = 0x1000;

    private const string LegacyIconFileName = ".geniafolder.ico";
    private const string DesktopIniFileName = "desktop.ini";
    private const string LegacyMetadataDirectoryName = ".geniafolder";
    private const int RetainedGeneratedIcons = 8;

    private readonly SemaphoreSlim _appearanceGate = new(1, 1);

    public async Task ApplyColorAsync(string folderPath, FolderColor color)
    {
        await _appearanceGate.WaitAsync();

        try
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException(folderPath);

            folderPath = Path.GetFullPath(folderPath);

            // Explorer caches folder icons aggressively by resource path.
            // Never overwrite/reuse the same .ico path for a new selection.
            var iconFileName = GetIconFileName(color);
            var iconPath = Path.Combine(folderPath, iconFileName);
            var desktopIniPath = Path.Combine(folderPath, DesktopIniFileName);

            await WriteColorIconAsync(
                iconPath,
                GetColor(color));

            File.SetAttributes(
                iconPath,
                FileAttributes.Hidden | FileAttributes.System);

            var ini =
                "[.ShellClassInfo]\r\n" +
                $"IconResource={iconFileName},0\r\n" +
                "IconIndex=0\r\n" +
                "ConfirmFileOp=0\r\n";

            await WriteDesktopIniAtomicAsync(
                desktopIniPath,
                ini);

            // desktop.ini is only honored for customized folders when the
            // shell customization attribute is present.
            PathMakeSystemFolder(folderPath);

            CleanupLegacyFlatIcon(folderPath);
            CleanupLegacyMetadataDirectory(folderPath);

            // Do NOT immediately delete the previous icon. Explorer may still
            // be reading a cached older desktop.ini; keeping a short history
            // prevents a transient fallback to the default folder icon.
            CleanupOldGeneratedIcons(
                folderPath,
                iconFileName);

            RefreshExplorer(folderPath);
        }
        finally
        {
            _appearanceGate.Release();
        }
    }

    public async Task RemoveCustomizationAsync(
        string folderPath)
    {
        await _appearanceGate.WaitAsync();

        try
        {
            if (!Directory.Exists(folderPath))
                return;

            folderPath = Path.GetFullPath(folderPath);

        var desktopIniPath = Path.Combine(
            folderPath,
            DesktopIniFileName);

        if (File.Exists(desktopIniPath))
        {
            var ownedByGeniaFolder = false;

            try
            {
                var text = await File.ReadAllTextAsync(
                    desktopIniPath,
                    Encoding.Unicode);

                ownedByGeniaFolder =
                    text.Contains(
                        ".geniafolder-",
                        StringComparison.OrdinalIgnoreCase) ||
                    text.Contains(
                        ".geniafolder.ico",
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // If it cannot be read, do not delete an unknown desktop.ini.
            }

            if (ownedByGeniaFolder)
            {
                PrepareForOverwrite(desktopIniPath);
                File.Delete(desktopIniPath);
            }
        }

        foreach (var icon in Directory.EnumerateFiles(
            folderPath,
            ".geniafolder*.ico",
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.SetAttributes(icon, FileAttributes.Normal);
                File.Delete(icon);
            }
            catch
            {
                throw new IOException(
                    $"Не удалось удалить старый значок GeniaFolder: {icon}");
            }
        }

        try
        {
            var attributes = File.GetAttributes(folderPath);
            if ((attributes & FileAttributes.System) != 0)
            {
                File.SetAttributes(
                    folderPath,
                    attributes & ~FileAttributes.System);
            }
        }
        catch
        {
            // Explorer refresh still runs; failure here is cosmetic.
        }

        RefreshExplorer(folderPath);
        }
        finally
        {
            _appearanceGate.Release();
        }
    }

    public static MediaColor GetColor(FolderColor color) => color switch
    {
        FolderColor.Blue => MediaColor.FromRgb(55, 126, 235),
        FolderColor.Green => MediaColor.FromRgb(52, 168, 83),
        FolderColor.Yellow => MediaColor.FromRgb(244, 180, 0),
        FolderColor.Orange => MediaColor.FromRgb(243, 124, 32),
        FolderColor.Red => MediaColor.FromRgb(218, 68, 83),
        FolderColor.Purple => MediaColor.FromRgb(142, 68, 173),
        _ => MediaColor.FromRgb(120, 124, 132)
    };

    private static string GetIconFileName(FolderColor color) =>
        $".geniafolder-{color.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}.ico";

    private static void RefreshExplorer(string folderPath)
    {
        var flags = SHCNF_PATHW | SHCNF_FLUSH;

        // The item itself changed (desktop.ini/icon resource).
        SHChangeNotify(SHCNE_ATTRIBUTES, flags, folderPath, IntPtr.Zero);
        SHChangeNotify(SHCNE_UPDATEITEM, flags, folderPath, IntPtr.Zero);

        // Explorer often renders a child folder from the parent view, so make
        // that view refresh immediately as well.
        var parent = Path.GetDirectoryName(folderPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            SHChangeNotify(SHCNE_UPDATEDIR, flags, parent, IntPtr.Zero);
    }

    private static void PrepareForOverwrite(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CleanupOldGeneratedIcons(
        string folderPath,
        string keepFileName)
    {
        FileInfo[] icons;

        try
        {
            icons = Directory
                .EnumerateFiles(
                    folderPath,
                    ".geniafolder-*.ico",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info =>
                    info.LastWriteTimeUtc)
                .ToArray();
        }
        catch
        {
            return;
        }

        var retained = 0;

        foreach (var icon in icons)
        {
            if (string.Equals(
                icon.Name,
                keepFileName,
                StringComparison.OrdinalIgnoreCase))
            {
                retained++;
                continue;
            }

            if (retained < RetainedGeneratedIcons)
            {
                retained++;
                continue;
            }

            try
            {
                File.SetAttributes(
                    icon.FullName,
                    FileAttributes.Normal);
                icon.Delete();
            }
            catch
            {
                // Old icon cleanup is cosmetic. Never break a successful
                // color change because Explorer still has a stale handle.
            }
        }
    }

    private static async Task WriteDesktopIniAtomicAsync(
        string desktopIniPath,
        string content)
    {
        var tempPath =
            desktopIniPath +
            ".tmp-" +
            Guid.NewGuid().ToString("N");

        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                content,
                Encoding.Unicode);

            if (File.Exists(desktopIniPath))
                PrepareForOverwrite(desktopIniPath);

            File.Move(
                tempPath,
                desktopIniPath,
                overwrite: true);

            File.SetAttributes(
                desktopIniPath,
                FileAttributes.Hidden | FileAttributes.System);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.SetAttributes(
                        tempPath,
                        FileAttributes.Normal);
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }

    private static void CleanupLegacyFlatIcon(string folderPath)
    {
        var legacyIcon = Path.Combine(folderPath, LegacyIconFileName);
        if (!File.Exists(legacyIcon))
            return;

        try
        {
            File.SetAttributes(legacyIcon, FileAttributes.Normal);
            File.Delete(legacyIcon);
        }
        catch
        {
            // Cosmetic migration only; never fail a color change because an
            // old hidden icon cannot be removed immediately.
        }
    }

    private static void CleanupLegacyMetadataDirectory(string folderPath)
    {
        var legacyDirectory = Path.Combine(folderPath, LegacyMetadataDirectoryName);
        if (!Directory.Exists(legacyDirectory))
            return;

        try
        {
            var entries = Directory.GetFileSystemEntries(legacyDirectory);
            if (entries.Length == 1 &&
                string.Equals(Path.GetFileName(entries[0]), "folder.ico", StringComparison.OrdinalIgnoreCase))
            {
                var legacyIcon = entries[0];
                if (File.Exists(legacyIcon))
                {
                    File.SetAttributes(legacyIcon, FileAttributes.Normal);
                    File.Delete(legacyIcon);
                }

                new DirectoryInfo(legacyDirectory).Attributes = FileAttributes.Normal;
                Directory.Delete(legacyDirectory);
            }
        }
        catch
        {
        }
    }

    private static async Task WriteColorIconAsync(string path, MediaColor color)
    {
        const int size = 64;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var body = new SolidColorBrush(color);
            body.Freeze();

            var tabColor = MediaColor.FromRgb(
                (byte)Math.Min(255, color.R + 18),
                (byte)Math.Min(255, color.G + 18),
                (byte)Math.Min(255, color.B + 18));

            var tab = new SolidColorBrush(tabColor);
            tab.Freeze();

            dc.DrawRoundedRectangle(body, null, new Rect(4, 18, 56, 39), 6, 6);
            dc.DrawRoundedRectangle(tab, null, new Rect(7, 9, 28, 19), 5, 5);
            dc.DrawRectangle(body, null, new Rect(4, 22, 56, 12));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var png = new MemoryStream();
        encoder.Save(png);
        var pngBytes = png.ToArray();

        await using var output = File.Create(path);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)size);
        writer.Write((byte)size);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)pngBytes.Length);
        writer.Write((uint)22);
        writer.Write(pngBytes);

        await output.FlushAsync();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PathMakeSystemFolder(string pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
}
