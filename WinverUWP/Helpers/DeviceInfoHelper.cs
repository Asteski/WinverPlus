using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.Storage;

namespace WinverUWP.Helpers;

internal static unsafe class DeviceInfoHelper
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public ulong AdapterLuid;
        public uint Flags;
    }

    public static List<(string Name, ulong Memory)> GetGraphicsAdapters()
    {
        var result = new List<(string, ulong)>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref iid, out var factory) < 0) return result;
        try
        {
            var table = *(void***)factory;
            var enumerate = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)table[12];
            for (uint index = 0; index < 256; index++)
            {
                IntPtr adapter;
                if (enumerate(factory, index, &adapter) < 0) break;
                try
                {
                    AdapterDescription description;
                    var getDescription = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDescription*, int>)(*(void***)adapter)[10];
                    if (getDescription(adapter, &description) < 0 || (description.Flags & 2) != 0) continue;
                    string name = new string(description.Description).Trim();
                    if (!string.IsNullOrEmpty(name)) result.Add((name, (ulong)description.DedicatedVideoMemory));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    public static (ulong Total, ulong Used) GetStorage()
    {
        ulong total = 0, used = 0;
        uint drives = GetLogicalDrives();
        for (int index = 0; index < 26; index++)
        {
            if ((drives & (1U << index)) == 0) continue;
            string root = ((char)('A' + index)) + @":\";
            if (GetDriveTypeW(root) != 3) continue; // Fixed local volumes only.
            bool readable = GetDiskFreeSpaceExW(root, out _, out var size, out var free);
            if (!readable && ApplicationData.Current.LocalFolder.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                readable = GetDiskFreeSpaceExW(ApplicationData.Current.LocalFolder.Path, out _, out size, out free);
            if (readable && size >= free) { total += size; used += size - free; }
        }
        return (total, used);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);

    public static string GetExperienceVersion()
    {
        foreach (string family in new[] { "MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "MicrosoftWindows.Client.Core_cw5n1h2txyewy" })
        {
            uint count = 0, length = 0;
            int status = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
            if (status != 122 || count == 0 || count > 100 || length > 65536) continue;
            IntPtr names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)length * 2));
            try
            {
                if (GetPackagesByPackageFamily(family, ref count, names, ref length, buffer) != 0) continue;
                var versions = new List<Version>();
                for (int i = 0; i < count; i++)
                {
                    string fullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size));
                    var parts = fullName?.Split('_');
                    if (parts?.Length > 1 && Version.TryParse(parts[1], out var version)) versions.Add(version);
                }
                if (versions.Count > 0) return versions.Max().ToString();
            }
            finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(names); }
        }
        // Inbox system packages may be present on disk without user registration.
        string windows = RegistryHelper.GetInfoString("SystemRoot") ?? @"C:\Windows";
        foreach (string family in new[] { "MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "MicrosoftWindows.Client.Core_cw5n1h2txyewy" })
        {
            try
            {
                var manifest = XDocument.Load(Path.Combine(windows, "SystemApps", family, "AppxManifest.xml"));
                string text = manifest.Root?.Element(manifest.Root.Name.Namespace + "Identity")?.Attribute("Version")?.Value;
                if (Version.TryParse(text, out var version)) return version.ToString();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Xml.XmlException)
            { System.Diagnostics.Debug.WriteLine(ex); }
        }
        return null;
    }
}
