using System.Security.Cryptography;
using System.Text;
using Zercraft.Core;

var safeDisk = new UsbDisk(
    3, "USB Drive", "SERIAL-1", "USB\\DISK-1", "USB", 16_000_000_000,
    false, false, false, false, "Online", "GPT");
Assert(DiskSafety.IsEligible(safeDisk), "An online writable USB disk should be eligible.");
Assert(!DiskSafety.IsEligible(safeDisk with { IsBoot = true }), "A boot disk must never be eligible.");
Assert(!DiskSafety.IsEligible(safeDisk with { IsSystem = true }), "A system disk must never be eligible.");
Assert(!DiskSafety.IsEligible(safeDisk with { BusType = "SATA" }), "A non-USB disk must never be eligible.");
Assert(!DiskSafety.IsEligible(safeDisk with { IsReadOnly = true }), "A read-only disk must never be eligible.");
Assert(!DiskSafety.IsEligible(safeDisk with { IsOffline = true }), "An offline disk must never be eligible.");
Assert(!DiskSafety.IsEligible(safeDisk with { OperationalStatus = "Offline" }), "A non-online disk must never be eligible.");
Assert(!DiskSafety.IsSameDevice(safeDisk, safeDisk with { SerialNumber = "SERIAL-2" }),
    "A different serial number must be rejected before writing.");
Assert(!DiskSafety.IsSameDevice(safeDisk, safeDisk with { UniqueId = "USB\\DISK-2" }),
    "A different unique ID must be rejected before writing.");
Assert(DiskSafety.ConfirmationPhrase(safeDisk) == "EFFACER 3",
    "The confirmation phrase must identify the target disk number.");

await using (var image = new MemoryStream([1, 2, 3, 4, 5]))
await using (var target = new MemoryStream())
{
    var reports = new List<long>();
    var written = await RawImageWriter.WriteAsync(
        image, target, image.Length, new InlineProgress<long>(reports.Add));
    Assert(written == 5 && target.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }),
        "The raw writer must copy the complete image.");
    Assert(reports.Count > 0 && reports[^1] == 5, "The raw writer must report progress.");
}

await using (var truncatedImage = new MemoryStream([1, 2]))
await using (var partialTarget = new MemoryStream())
{
    await AssertThrowsAsync<EndOfStreamException>(
        () => RawImageWriter.WriteAsync(truncatedImage, partialTarget, 3),
        "A truncated ISO must fail.");
}

var tempRoot = Path.Combine(Path.GetTempPath(), $"zercraft-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);
try
{
    var sourceOne = Path.Combine(tempRoot, "notes.txt");
    var sourceTwo = Path.Combine(tempRoot, "report.txt");
    var backup = Path.Combine(tempRoot, "backup.zercraftbackup");
    await File.WriteAllTextAsync(sourceOne, "private note");
    await File.WriteAllTextAsync(sourceTwo, "quarterly report");

    await BackupArchive.CreateAsync([sourceOne, sourceTwo], backup, "correct horse battery");
    var rawBackup = await File.ReadAllBytesAsync(backup);
    Assert(!System.Text.Encoding.UTF8.GetString(rawBackup).Contains("private note", StringComparison.Ordinal),
        "Backup plaintext must not be visible in the encrypted file.");

    var restoreRoot = Path.Combine(tempRoot, "restore");
    var restored = await BackupArchive.RestoreAsync(backup, restoreRoot, "correct horse battery");
    Assert(File.ReadAllText(Path.Combine(restored, "Fichiers", "00001-notes.txt")) == "private note",
        "Restore must recover the first file.");
    Assert(File.ReadAllText(Path.Combine(restored, "Fichiers", "00002-report.txt")) == "quarterly report",
        "Restore must recover the second file.");
    Assert(File.ReadAllText(sourceOne) == "private note", "Backup/restore must preserve the original.");

    var failedRestoreRoot = Path.Combine(tempRoot, "bad-password-restore");
    await AssertThrowsAsync<CryptographicException>(
        () => BackupArchive.RestoreAsync(backup, failedRestoreRoot, "incorrect password"),
        "A wrong password must fail authentication.");
    Assert(!Directory.Exists(failedRestoreRoot) || !Directory.EnumerateFileSystemEntries(failedRestoreRoot).Any(),
        "A wrong password must not extract files.");

    var tampered = Path.Combine(tempRoot, "tampered.zercraftbackup");
    rawBackup[^1] ^= 0x80;
    await File.WriteAllBytesAsync(tampered, rawBackup);
    await AssertThrowsAsync<CryptographicException>(
        () => BackupArchive.RestoreAsync(tampered, Path.Combine(tempRoot, "tampered-restore"),
            "correct horse battery"),
        "A modified backup must fail authentication.");

    var isoBytes = new byte[] { 9, 8, 7, 6, 5 };
    var checksum = Convert.ToHexString(SHA256.HashData(isoBytes)).ToLowerInvariant();
    var isoSource = new IsoSource(
        "Test ISO",
        "Test source",
        new Uri("https://releases.ubuntu.com/test/"),
        new Uri("https://releases.ubuntu.com/test/test.iso"),
        new Uri("https://releases.ubuntu.com/test/SHA256SUMS"),
        "test.iso",
        "Ubuntu");
    using var httpClient = new HttpClient(new StaticResponseHandler(request =>
    {
        var isWrongFile = request.RequestUri!.AbsolutePath.Contains("wrong", StringComparison.Ordinal);
        var isManifest = request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal);
        var content = isManifest
            ? new StringContent(
                isWrongFile ? $"{new string('0', 64)} *wrong.iso\n" : $"{checksum} *test.iso\n",
                Encoding.ASCII)
            : new ByteArrayContent(isWrongFile ? [1, 1, 1, 1, 1] : isoBytes);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = content,
            RequestMessage = request
        };
    }));
    var isoPath = Path.Combine(tempRoot, "test.iso");
    await new IsoDownloadService(httpClient).DownloadVerifiedAsync(isoSource, isoPath);
    Assert((await File.ReadAllBytesAsync(isoPath)).SequenceEqual(isoBytes),
        "Official-source download must be saved after checksum verification.");

    var wrongChecksumSource = isoSource with
    {
        FileName = "wrong.iso",
        DownloadUri = new Uri("https://releases.ubuntu.com/test/wrong.iso"),
        Sha256ManifestUri = new Uri("https://releases.ubuntu.com/test/wrong-SHA256SUMS")
    };
    var wrongChecksumPath = Path.Combine(tempRoot, "wrong.iso");
    await AssertThrowsAsync<CryptographicException>(
        () => new IsoDownloadService(httpClient).DownloadVerifiedAsync(wrongChecksumSource, wrongChecksumPath),
        "A checksum mismatch must fail.");
    Assert(!File.Exists(wrongChecksumPath) && !File.Exists(wrongChecksumPath + ".partial"),
        "A checksum mismatch must not leave a usable or partial ISO.");
}
finally
{
    Directory.Delete(tempRoot, recursive: true);
}

Console.WriteLine("All Zercraft safety, writer, and encrypted-backup tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(createResponse(request));
    }
}
