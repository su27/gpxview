using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Security.Credentials;

namespace GpxView.App;

internal interface IRoadNetworkCredentialStore
{
    string? ReadDeviceToken(Uri endpoint);
    void SaveDeviceToken(Uri endpoint, string token);
    void DeleteDeviceToken(Uri endpoint);
}

internal sealed class WindowsCredentialLockerRoadNetworkStore : IRoadNetworkCredentialStore
{
    private const string UserName = "device";
    private const int ErrorInsufficientBuffer = 122;
    private readonly WindowsCredentialManagerStore credentialManager = new();
    private PasswordVault? vault;
    private bool lockerUnavailable = !HasPackageIdentity();

    public string? ReadDeviceToken(Uri endpoint)
    {
        if (!lockerUnavailable)
        {
            try
            {
                var credential = GetVault().Retrieve(LockerResource(endpoint), UserName);
                credential.RetrievePassword();
                if (!string.IsNullOrWhiteSpace(credential.Password)) return credential.Password;
            }
            catch (Exception exception) when (IsCredentialMissing(exception))
            {
                // Unpackaged builds may have stored the token through Credential Manager instead.
            }
            catch (Exception exception) when (IsLockerUnavailable(exception))
            {
                lockerUnavailable = true;
            }
        }
        return credentialManager.Read(endpoint);
    }

    public void SaveDeviceToken(Uri endpoint, string token)
    {
        if (!lockerUnavailable)
        {
            try
            {
                DeleteFromLocker(endpoint);
                GetVault().Add(new PasswordCredential(LockerResource(endpoint), UserName, token));
                credentialManager.Delete(endpoint);
                return;
            }
            catch (Exception exception) when (IsLockerUnavailable(exception))
            {
                lockerUnavailable = true;
            }
        }
        credentialManager.Save(endpoint, token);
    }

    public void DeleteDeviceToken(Uri endpoint)
    {
        if (!lockerUnavailable)
        {
            try
            {
                DeleteFromLocker(endpoint);
            }
            catch (Exception exception) when (IsLockerUnavailable(exception))
            {
                lockerUnavailable = true;
            }
        }
        credentialManager.Delete(endpoint);
    }

    private PasswordVault GetVault() => vault ??= new PasswordVault();

    private void DeleteFromLocker(Uri endpoint)
    {
        try
        {
            GetVault().Remove(GetVault().Retrieve(LockerResource(endpoint), UserName));
        }
        catch (Exception exception) when (IsCredentialMissing(exception))
        {
            // Removing an already absent credential is idempotent.
        }
    }

    private static string LockerResource(Uri endpoint) => $"GpxView Road Network {endpoint.AbsoluteUri}";

    private static bool IsCredentialMissing(Exception exception) =>
        exception is COMException
        && (exception.HResult == unchecked((int)0x80070490)
            || exception.HResult == unchecked((int)0x80070002));

    private static bool IsLockerUnavailable(Exception exception) =>
        exception is COMException
            or InvalidOperationException
            or NotSupportedException
            or PlatformNotSupportedException
            or TypeInitializationException
            or UnauthorizedAccessException;

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) == ErrorInsufficientBuffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}

internal sealed class WindowsCredentialManagerStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string? Read(Uri endpoint)
    {
        if (!CredRead(Target(endpoint), CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = Encoding.Unicode.GetString(bytes);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Save(Uri endpoint, string token)
    {
        var bytes = Encoding.Unicode.GetBytes(token);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = Target(endpoint),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = "GpxView"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(Uri endpoint)
    {
        if (CredDelete(Target(endpoint), CredentialTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound) throw new Win32Exception(error);
    }

    private static string Target(Uri endpoint) => $"GpxView/RoadNetwork/{endpoint.AbsoluteUri}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
