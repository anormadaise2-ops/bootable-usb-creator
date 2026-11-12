namespace Zercraft.Core;

public sealed record UsbDisk(
    int Number,
    string FriendlyName,
    string SerialNumber,
    string UniqueId,
    string BusType,
    long SizeBytes,
    bool IsBoot,
    bool IsSystem,
    bool IsReadOnly,
    bool IsOffline,
    string OperationalStatus,
    string PartitionStyle)
{
    public string DisplayName =>
        $"Disque {Number} — {FriendlyName} — {FormatSize(SizeBytes)}";

    private static string FormatSize(long sizeBytes) =>
        sizeBytes >= 1_000_000_000
            ? $"{sizeBytes / 1_000_000_000d:0.##} Go"
            : $"{sizeBytes / 1_000_000d:0.##} Mo";
}

public static class DiskSafety
{
    public static bool IsEligible(UsbDisk disk) =>
        string.Equals(disk.BusType, "USB", StringComparison.OrdinalIgnoreCase)
        && disk.Number >= 0
        && disk.SizeBytes > 0
        && !disk.IsBoot
        && !disk.IsSystem
        && !disk.IsReadOnly
        && !disk.IsOffline
        && string.Equals(disk.OperationalStatus, "Online", StringComparison.OrdinalIgnoreCase);

    public static bool IsSameDevice(UsbDisk selected, UsbDisk current) =>
        selected.Number == current.Number
        && selected.SizeBytes == current.SizeBytes
        && string.Equals(selected.FriendlyName, current.FriendlyName, StringComparison.Ordinal)
        && string.Equals(selected.BusType, current.BusType, StringComparison.OrdinalIgnoreCase)
        && IdentityMatches(selected.SerialNumber, current.SerialNumber)
        && IdentityMatches(selected.UniqueId, current.UniqueId);

    public static string ConfirmationPhrase(UsbDisk disk) => $"EFFACER {disk.Number}";

    private static bool IdentityMatches(string selected, string current)
    {
        if (string.IsNullOrWhiteSpace(selected) && string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        return string.Equals(selected.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
