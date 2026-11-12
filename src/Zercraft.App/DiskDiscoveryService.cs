using System.Diagnostics;
using System.Text.Json;
using Zercraft.Core;

namespace Zercraft.App;

public sealed class DiskDiscoveryService
{
    private const string DiskQuery =
        "try { $disks = @(Get-Disk -ErrorAction Stop | Select-Object " +
        "@{Name='Number';Expression={[int]$_.Number}}, " +
        "@{Name='FriendlyName';Expression={[string]$_.FriendlyName}}, " +
        "@{Name='SerialNumber';Expression={[string]$_.SerialNumber}}, " +
        "@{Name='UniqueId';Expression={[string]$_.UniqueId}}, " +
        "@{Name='BusType';Expression={[string]$_.BusType}}, " +
        "@{Name='SizeBytes';Expression={[long]$_.Size}}, " +
        "@{Name='IsBoot';Expression={[bool]$_.IsBoot}}, " +
        "@{Name='IsSystem';Expression={[bool]$_.IsSystem}}, " +
        "@{Name='IsReadOnly';Expression={[bool]$_.IsReadOnly}}, " +
        "@{Name='IsOffline';Expression={[bool]$_.IsOffline}}, " +
        "@{Name='OperationalStatus';Expression={[string]($_.OperationalStatus -join ', ')}}, " +
        "@{Name='PartitionStyle';Expression={[string]$_.PartitionStyle}}); " +
        "ConvertTo-Json -InputObject $disks -Compress; exit 0 } " +
        "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";

    public async Task<IReadOnlyList<UsbDisk>> GetUsbDisksAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(DiskQuery);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Impossible de lancer PowerShell pour détecter les disques.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows n’a pas pu détecter les disques : {error.Trim()}");
        }

        using var document = JsonDocument.Parse(output);
        var disks = new List<UsbDisk>();
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : document.RootElement.ValueKind == JsonValueKind.Object
                ? [document.RootElement]
                : [];
        foreach (var item in elements)
        {
            var disk = new UsbDisk(
                item.GetProperty("Number").GetInt32(),
                item.GetProperty("FriendlyName").GetString() ?? string.Empty,
                item.GetProperty("SerialNumber").GetString() ?? string.Empty,
                item.GetProperty("UniqueId").GetString() ?? string.Empty,
                item.GetProperty("BusType").GetString() ?? string.Empty,
                item.GetProperty("SizeBytes").GetInt64(),
                item.GetProperty("IsBoot").GetBoolean(),
                item.GetProperty("IsSystem").GetBoolean(),
                item.GetProperty("IsReadOnly").GetBoolean(),
                item.GetProperty("IsOffline").GetBoolean(),
                item.GetProperty("OperationalStatus").GetString() ?? string.Empty,
                item.GetProperty("PartitionStyle").GetString() ?? string.Empty);
            if (string.Equals(disk.BusType, "USB", StringComparison.OrdinalIgnoreCase))
            {
                disks.Add(disk);
            }
        }

        return disks.OrderBy(disk => disk.Number).ToArray();
    }
}
