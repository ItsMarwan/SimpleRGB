using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace SimpleRGB.Core.Platform;

public enum MemoryGeneration { Unknown, Ddr3, Ddr4, Ddr5 }

/// <summary>Motherboard / BIOS / memory facts, read from the registry and SMBIOS (no WMI dependency).</summary>
public static class SystemInfo
{
    private static readonly Lazy<(string Manufacturer, string Product, string BiosVendor, string BiosVersion)> Board = new(ReadBoard);
    private static readonly Lazy<MemoryGeneration> Memory = new(ReadMemoryGeneration);

    public static string MotherboardManufacturer => Board.Value.Manufacturer;
    public static string MotherboardModel => Board.Value.Product;
    public static string BiosVendor => Board.Value.BiosVendor;
    public static string BiosVersion => Board.Value.BiosVersion;
    public static MemoryGeneration MemoryGeneration => Memory.Value;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static (string, string, string, string) ReadBoard()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string Get(string name) => (key?.GetValue(name) as string)?.Trim() ?? "";
            var product = Get("BaseBoardProduct");
            return (Get("BaseBoardManufacturer"), product.Length > 0 ? product : "Unknown", Get("BIOSVendor"), Get("BIOSVersion"));
        }
        catch
        {
            return ("", "Unknown", "", "");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);

    /// <summary>Reads SMBIOS type 17 (Memory Device) records to learn the DDR generation.</summary>
    private static MemoryGeneration ReadMemoryGeneration()
    {
        try
        {
            const uint RSMB = 0x52534D42; // 'RSMB'
            var size = GetSystemFirmwareTable(RSMB, 0, null, 0);
            if (size == 0) return MemoryGeneration.Unknown;
            var buf = new byte[size];
            if (GetSystemFirmwareTable(RSMB, 0, buf, size) != size) return MemoryGeneration.Unknown;

            // RawSMBIOSData header is 8 bytes; then the structure table.
            var tableLength = BitConverter.ToInt32(buf, 4);
            var pos = 8;
            var end = Math.Min(buf.Length, 8 + tableLength);
            var found = MemoryGeneration.Unknown;
            while (pos + 4 <= end)
            {
                var type = buf[pos];
                var length = buf[pos + 1];
                if (length < 4) break;
                if (type == 17 && length > 0x12 && pos + 0x12 < end)
                {
                    var sizeField = BitConverter.ToUInt16(buf, pos + 0x0C);
                    var memType = buf[pos + 0x12];
                    if (sizeField != 0) // 0 = empty slot
                    {
                        found = memType switch
                        {
                            0x18 => MemoryGeneration.Ddr3,
                            0x1A => MemoryGeneration.Ddr4,
                            0x22 => MemoryGeneration.Ddr5,
                            _ => found,
                        };
                    }
                }
                if (type == 127) break; // end-of-table

                // Skip formatted area, then the string set terminated by a double NUL.
                var next = pos + length;
                while (next + 1 < end && !(buf[next] == 0 && buf[next + 1] == 0)) next++;
                pos = next + 2;
            }
            return found;
        }
        catch
        {
            return MemoryGeneration.Unknown;
        }
    }
}
