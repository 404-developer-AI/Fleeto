namespace Fleeto.Core.Domain;

/// <summary>
/// The files of Fleeto Connect (0.8.0, PROXMOX-WEB.md §4) as the web image carries them below <c>Connect:FilesDirectory</c> and the release
/// manifest lists them: one per operating system the technician's PC can run.
/// </summary>
public static class ConnectDownloads
{
    /// <param name="Platform">The key in a download address: <c>windows</c> or <c>ubuntu</c>.</param>
    /// <param name="Path">Relative to the files directory.</param>
    /// <param name="DownloadName">The name the browser saves the file as; <c>{version}</c> is the Fleeto version.</param>
    public sealed record File(string Platform, string Label, string Path, string DownloadName, string ContentType);

    public static readonly IReadOnlyList<File> Files =
    [
        new("windows", "Windows 10 and 11 (x64)", "windows-amd64/fleeto-connect.exe", "fleeto-connect.exe", "application/vnd.microsoft.portable-executable"),
        new("ubuntu", "Ubuntu desktop 24.04 or newer (x64)", "linux-amd64/fleeto-connect_amd64.deb", "fleeto-connect_{version}_amd64.deb",
            "application/vnd.debian.binary-package"),
    ];

    public static File? Find(string platform) => Files.FirstOrDefault(f => f.Platform == platform);
}
