using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GeniaFolder.Services;

public sealed class FolderIdentityService
{
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileIdInfoClass = 18;

    public FolderIdentity? TryGetIdentity(string path)
    {
        if (!Directory.Exists(path))
            return null;

        using var handle = CreateFileW(
            path,
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        if (!GetFileInformationByHandleEx(
            handle,
            FileIdInfoClass,
            out FILE_ID_INFO info,
            Marshal.SizeOf<FILE_ID_INFO>()))
        {
            return null;
        }

        return new FolderIdentity(
            info.VolumeSerialNumber,
            $"{info.FileId.Part1:X16}{info.FileId.Part2:X16}");
    }

    public bool Matches(
        string path,
        ulong volumeSerialNumber,
        string fileId)
    {
        if (volumeSerialNumber == 0 ||
            string.IsNullOrWhiteSpace(fileId))
        {
            return false;
        }

        var identity = TryGetIdentity(path);
        return identity is not null &&
               identity.VolumeSerialNumber == volumeSerialNumber &&
               string.Equals(
                   identity.FileId,
                   fileId,
                   StringComparison.OrdinalIgnoreCase);
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FILE_ID_INFO lpFileInformation,
        int dwBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_128
    {
        public ulong Part1;
        public ulong Part2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public FILE_ID_128 FileId;
    }
}

public sealed record FolderIdentity(
    ulong VolumeSerialNumber,
    string FileId);
