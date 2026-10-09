using System;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

// Read-only desktop inventory; UWP cannot query capacity on every fixed volume.
internal static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder name);

    private static void Main()
    {
        try
        {
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != 122) return;
            var family = new StringBuilder((int)length);
            if (GetCurrentPackageFamilyName(ref length, family) != 0) return;
            ulong used = 0, volumeTotal = 0, physicalTotal = 0;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                ulong size = (ulong)drive.TotalSize;
                ulong free = (ulong)drive.TotalFreeSpace;
                volumeTotal += size;
                used += size - free;
            }
            using (var query = new ManagementObjectSearcher("SELECT Size, MediaType, InterfaceType FROM Win32_DiskDrive"))
            using (var disks = query.Get())
                foreach (ManagementObject disk in disks)
                    using (disk)
                        if (Convert.ToString(disk["MediaType"]).IndexOf("Fixed", StringComparison.OrdinalIgnoreCase) >= 0)
                            physicalTotal += Convert.ToUInt64(disk["Size"], CultureInfo.InvariantCulture);
            ulong total = Math.Max(volumeTotal, physicalTotal);
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family.ToString(), "LocalState");
            string target = Path.Combine(folder, "storage-inventory.json");
            string json = "{\"CapturedUtc\":\"" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\",\"Total\":" + total.ToString(CultureInfo.InvariantCulture) + ",\"Used\":" + used.ToString(CultureInfo.InvariantCulture) + "}";
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        catch (Exception ex)
        { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
