using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class FolderAppearanceService
{
    private const uint SHCNE_ATTRIBUTES = 0x00000800;
    private const uint SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNF_PATHW = 0x0005;

    private const string IconFileName = ".geniafolder.ico";
    private const string DesktopIniFileName = "desktop.ini";
    private const string LegacyMetadataDirectoryName = ".geniafolder";

    public async Task ApplyColorAsync(string folderPath, FolderColor color)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(folderPath);

        var iconPath = Path.Combine(folderPath, IconFileName);
        var desktopIniPath = Path.Combine(folderPath, DesktopIniFileName);

        PrepareForOverwrite(iconPath);
        PrepareForOverwrite(desktopIniPath);

        await WriteColorIconAsync(iconPath, GetColor(color));

        var ini = "[.ShellClassInfo]\r\n" +
                  $"IconResource={IconFileName},0\r\n" +
                  "IconIndex=0\r\n" +
                  "ConfirmFileOp=0\r\n";
        await File.WriteAllTextAsync(desktopIniPath, ini, Encoding.Unicode);

        File.SetAttributes(iconPath, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);

        PathMakeSystemFolder(folderPath);
        CleanupLegacyMetadataDirectory(folderPath);

        SHChangeNotify(SHCNE_ATTRIBUTES, SHCNF_PATHW, folderPath, IntPtr.Zero);
        SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, folderPath, IntPtr.Zero);
    }

    public static Color GetColor(FolderColor color) => color switch
    {
        FolderColor.Blue => Color.FromRgb(55, 126, 235),
        FolderColor.Green => Color.FromRgb(52, 168, 83),
        FolderColor.Yellow => Color.FromRgb(244, 180, 0),
        FolderColor.Orange => Color.FromRgb(243, 124, 32),
        FolderColor.Red => Color.FromRgb(218, 68, 83),
        FolderColor.Purple => Color.FromRgb(142, 68, 173),
        _ => Color.FromRgb(120, 124, 132)
    };

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

    private static async Task WriteColorIconAsync(string path, Color color)
    {
        const int size = 64;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var body = new SolidColorBrush(color);
            body.Freeze();
            var tabColor = Color.FromRgb(
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
