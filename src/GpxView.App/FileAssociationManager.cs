using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GpxView.App;

internal static class FileAssociationManager
{
    private const string ProgId = "GpxView.Track";
    private const string ApplicationName = "GpxView";
    private const string ApplicationExeName = "GpxView.exe";
    private const string TrackFileDescription = "GPS track file";

    public static readonly string[] SupportedExtensions = [".gpx", ".kml", ".kmz", ".fit"];
    private static bool CanWriteAssociations =>
        !string.Equals(BuildInfo.Channel, "Store", StringComparison.Ordinal);

    public static FileAssociationStatus[] GetStatuses() =>
        SupportedExtensions.Select(extension => new FileAssociationStatus(
            extension,
            extension.TrimStart('.').ToUpperInvariant(),
            IsAssociated(extension),
            CanWriteAssociations && !HasProtectedUserChoice(extension))).ToArray();

    public static FileAssociationUpdateResult Associate(string extension)
    {
        if (!TryNormalizeExtension(extension, out var normalized))
            return new FileAssociationUpdateResult(FileAssociationUpdateStatus.Failed, extension);
        if (!CanWriteAssociations)
            return new FileAssociationUpdateResult(FileAssociationUpdateStatus.NeedsSystemConfirmation, normalized);

        try
        {
            RegisterApplication();
            using var extensionKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{normalized}");
            extensionKey?.SetValue(string.Empty, ProgId, RegistryValueKind.String);
            using var openWithKey =
                Registry.CurrentUser.CreateSubKey($@"Software\Classes\{normalized}\OpenWithProgids");
            openWithKey?.SetValue(ProgId, string.Empty, RegistryValueKind.String);
            NotifyAssociationChanged();
            return new FileAssociationUpdateResult(
                IsAssociated(normalized)
                    ? FileAssociationUpdateStatus.Associated
                    : FileAssociationUpdateStatus.NeedsSystemConfirmation,
                normalized);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                             or IOException
                                             or System.Security.SecurityException
                                             or InvalidOperationException)
        {
            return new FileAssociationUpdateResult(
                FileAssociationUpdateStatus.Failed,
                normalized,
                exception.HResult.ToString("X8"));
        }
    }

    private static void RegisterApplication()
    {
        var executablePath = Environment.ProcessPath
                             ?? Process.GetCurrentProcess().MainModule?.FileName
                             ?? Path.Combine(AppContext.BaseDirectory, ApplicationExeName);
        var command = $"\"{executablePath}\" \"%1\"";
        var icon = $"\"{executablePath}\",0";

        using var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}");
        progIdKey?.SetValue(string.Empty, TrackFileDescription, RegistryValueKind.String);
        progIdKey?.SetValue("FriendlyTypeName", TrackFileDescription, RegistryValueKind.String);
        using (var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
            iconKey?.SetValue(string.Empty, icon, RegistryValueKind.String);
        using (var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
            commandKey?.SetValue(string.Empty, command, RegistryValueKind.String);

        using var applicationKey =
            Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ApplicationExeName}");
        applicationKey?.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
        using (var applicationCommandKey =
               Registry.CurrentUser.CreateSubKey(
                   $@"Software\Classes\Applications\{ApplicationExeName}\shell\open\command"))
            applicationCommandKey?.SetValue(string.Empty, command, RegistryValueKind.String);

        using var supportedTypesKey =
            Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ApplicationExeName}\SupportedTypes");
        using var capabilitiesKey = Registry.CurrentUser.CreateSubKey(@"Software\GpxView\Capabilities");
        capabilitiesKey?.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
        capabilitiesKey?.SetValue(
            "ApplicationDescription",
            "View GPX, KML, KMZ and FIT tracks",
            RegistryValueKind.String);
        using var capabilityAssociationsKey =
            Registry.CurrentUser.CreateSubKey(@"Software\GpxView\Capabilities\FileAssociations");

        foreach (var supportedExtension in SupportedExtensions)
        {
            supportedTypesKey?.SetValue(supportedExtension, string.Empty, RegistryValueKind.String);
            capabilityAssociationsKey?.SetValue(supportedExtension, ProgId, RegistryValueKind.String);
            using var openWithKey =
                Registry.CurrentUser.CreateSubKey($@"Software\Classes\{supportedExtension}\OpenWithProgids");
            openWithKey?.SetValue(ProgId, string.Empty, RegistryValueKind.String);
        }

        using var registeredApplicationsKey = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registeredApplicationsKey?.SetValue(ApplicationName, @"Software\GpxView\Capabilities", RegistryValueKind.String);
    }

    private static bool IsAssociated(string extension)
    {
        if (!TryNormalizeExtension(extension, out var normalized)) return false;
        var associatedExecutable = QueryAssociatedExecutable(normalized);
        if (IsGpxViewExecutable(associatedExecutable)) return true;

        var userChoice = ReadCurrentUserString(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{normalized}\UserChoice",
            "ProgId");
        if (!string.IsNullOrWhiteSpace(userChoice)) return IsGpxViewProgId(userChoice);

        var userDefault = ReadCurrentUserString($@"Software\Classes\{normalized}", string.Empty);
        if (IsGpxViewProgId(userDefault)) return true;

        using var classesRootKey = Registry.ClassesRoot.OpenSubKey(normalized);
        return IsGpxViewProgId(classesRootKey?.GetValue(string.Empty) as string);
    }

    private static bool HasProtectedUserChoice(string extension)
    {
        if (!TryNormalizeExtension(extension, out var normalized)) return false;
        var userChoice = ReadCurrentUserString(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{normalized}\UserChoice",
            "ProgId");
        return !string.IsNullOrWhiteSpace(userChoice) && !IsGpxViewProgId(userChoice);
    }

    private static string? QueryAssociatedExecutable(string extension)
    {
        try
        {
            uint length = 0;
            var result = AssocQueryString(0, AssocStr.Executable, extension, null, null, ref length);
            if (length == 0) return null;
            var builder = new StringBuilder((int)length);
            result = AssocQueryString(0, AssocStr.Executable, extension, null, builder, ref length);
            return result == 0 ? builder.ToString() : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                             or EntryPointNotFoundException
                                             or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadCurrentUserString(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    private static bool TryNormalizeExtension(string extension, out string normalized)
    {
        normalized = extension.Trim();
        if (normalized.Length == 0) return false;
        if (!normalized.StartsWith('.')) normalized = "." + normalized;
        normalized = normalized.ToLowerInvariant();
        return SupportedExtensions.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGpxViewExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetFileName(path), ApplicationExeName, StringComparison.OrdinalIgnoreCase);

    private static bool IsGpxViewProgId(string? progId) =>
        string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(progId, $@"Applications\{ApplicationExeName}", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(progId)
            && progId.Contains("GpxView", StringComparison.OrdinalIgnoreCase));

    private static void NotifyAssociationChanged() =>
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(
        uint flags,
        AssocStr str,
        string pszAssoc,
        string? pszExtra,
        StringBuilder? pszOut,
        ref uint pcchOut);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private enum AssocStr
    {
        Executable = 2
    }
}

internal sealed record FileAssociationStatus(
    string Extension,
    string DisplayName,
    bool Associated,
    bool CanAssociate);

internal enum FileAssociationUpdateStatus
{
    Associated,
    NeedsSystemConfirmation,
    Failed
}

internal sealed record FileAssociationUpdateResult(
    FileAssociationUpdateStatus Status,
    string Extension,
    string? Error = null);
