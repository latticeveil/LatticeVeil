using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace LatticeVeilMonoGame.Core;

public enum AudioDeviceFlow
{
    Render,
    Capture
}

public sealed class AudioDeviceInfo
{
    public string Id { get; }
    public string Name { get; }
    public bool IsDefault { get; }

    public AudioDeviceInfo(string id, string name, bool isDefault)
    {
        Id = id;
        Name = name;
        IsDefault = isDefault;
    }
}

public static class AudioDeviceEnumerator
{
    private const int DeviceStateActive = 0x00000001;
    private const int DeviceStateMask = DeviceStateActive;
    private const int StgmRead = 0x00000000;
    private const int CoInitMultithreaded = 0x0;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly PROPERTYKEY PkeyDeviceFriendlyName =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        var uninitCom = false;
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            uninitCom = TryInitializeCom();
            enumerator = CreateEnumerator();
            var dataFlow = flow == AudioDeviceFlow.Render ? EDataFlow.eRender : EDataFlow.eCapture;
            var defaultId = TryGetDefaultDeviceId(enumerator, dataFlow);

            if (enumerator.EnumAudioEndpoints(dataFlow, DeviceStateMask, out collection) == 0 && collection != null)
            {
                collection.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice? device = null;
                    IPropertyStore? store = null;
                    try
                    {
                        collection.Item(i, out device);
                        if (device == null)
                            continue;

                        device.GetId(out var id);
                        device.OpenPropertyStore(StgmRead, out store);

                        var name = GetDeviceName(store) ?? "UNKNOWN DEVICE";
                        var isDefault = !string.IsNullOrEmpty(defaultId) &&
                            string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                        result.Add(new AudioDeviceInfo(id, name, isDefault));
                        if (!string.IsNullOrWhiteSpace(id))
                            knownIds.Add(id);
                    }
                    catch
                    {
                        // Best-effort enumeration; skip devices that fail.
                    }
                    finally
                    {
                        if (store != null) Marshal.ReleaseComObject(store);
                        if (device != null) Marshal.ReleaseComObject(device);
                    }
                }
            }
        }
        catch
        {
            // Best-effort enumeration; fall through to NAudio fallback.
        }
        finally
        {
            if (collection != null) Marshal.ReleaseComObject(collection);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            if (uninitCom) CoUninitialize();
        }

        TryAddDevicesViaNAudio(result, knownIds, flow);

        return result;
    }

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator, throwOnError: true);
        if (type == null)
            throw new InvalidOperationException("MMDeviceEnumerator COM type not available.");
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    private static string? TryGetDefaultDeviceId(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        var roles = new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications };
        foreach (var role in roles)
        {
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device == null)
                    continue;

                try
                {
                    device.GetId(out var id);
                    if (!string.IsNullOrWhiteSpace(id))
                        return id;
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            catch
            {
                // Try next role.
            }
        }

        return null;
    }

    private static string? GetDeviceName(IPropertyStore? store)
    {
        if (store == null)
            return null;

        var key = PkeyDeviceFriendlyName;
        if (store.GetValue(ref key, out var value) != 0)
            return null;

        try
        {
            return value.GetString();
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static void TryAddDevicesViaNAudio(List<AudioDeviceInfo> result, HashSet<string> knownIds, AudioDeviceFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var dataFlow = flow == AudioDeviceFlow.Render ? DataFlow.Render : DataFlow.Capture;
            var defaultId = TryGetDefaultDeviceId(enumerator, dataFlow);
            var devices = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);

            foreach (var device in devices)
            {
                if (device == null)
                    continue;

                var id = device.ID ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(id) && knownIds.Contains(id))
                    continue;

                var name = string.IsNullOrWhiteSpace(device.FriendlyName) ? "UNKNOWN DEVICE" : device.FriendlyName;
                var isDefault = !string.IsNullOrEmpty(defaultId) &&
                    string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);

                result.Add(new AudioDeviceInfo(id, name, isDefault));
                if (!string.IsNullOrWhiteSpace(id))
                    knownIds.Add(id);
            }
        }
        catch
        {
            // Ignore NAudio enumeration failures; COM path already returned anything it could.
        }
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var roles = new[] { Role.Console, Role.Multimedia, Role.Communications };
        foreach (var role in roles)
        {
            try
            {
                var device = enumerator.GetDefaultAudioEndpoint(flow, role);
                if (device != null)
                    return device.ID;
            }
            catch
            {
                // Try next role.
            }
        }

        return null;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private static bool TryInitializeCom()
    {
        var hr = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (hr == S_OK || hr == S_FALSE)
            return true;
        if (hr == RpcEChangedMode)
            return false;
        return false;
    }

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;

        public string? GetString()
        {
            const ushort VtLpwstr = 31;
            if (vt != VtLpwstr || pointerValue == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(pointerValue);
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        int RegisterEndpointNotificationCallback(IntPtr pClient);
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0C5E7AACC6A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint pcDevices);
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }
}
