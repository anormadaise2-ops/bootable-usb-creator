using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Zercraft.Core;

public static class BackupArchive
{
    public const int MinimumPasswordLength = 8;
    public const int Pbkdf2Iterations = 600_000;
    private const int SaltLength = 16;
    private const int IvLength = 16;
    private const int TagLength = 32;
    private const int HeaderLength = 8 + sizeof(int) + SaltLength + IvLength;
    private const long MaximumRestoreBytes = 100L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ZERCBA01");

    public static async Task CreateAsync(
        IReadOnlyList<string> sourceFiles,
        string destinationPath,
        string password,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ValidatePassword(password);
        if (sourceFiles.Count == 0)
        {
            throw new ArgumentException("Choisissez au moins un fichier à sauvegarder.", nameof(sourceFiles));
        }

        var partialPath = destinationPath + ".partial";
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var key = DeriveKey(password, salt);
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.AsSpan(32, 32).ToArray());
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key.AsSpan(0, 32).ToArray();
            aes.IV = iv;

            var header = CreateHeader(salt, iv);
            await using (var output = new FileStream(
                partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(header, cancellationToken);
                hmac.AppendData(header);
                var authenticatedOutput = new AuthenticatedWriteStream(output, hmac);
                using (var encrypted = new CryptoStream(
                    authenticatedOutput, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
                {
                    using (var archive = new ZipArchive(encrypted, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        for (var index = 0; index < sourceFiles.Count; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var sourcePath = Path.GetFullPath(sourceFiles[index]);
                            var fileName = Path.GetFileName(sourcePath);
                            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(sourcePath))
                            {
                                throw new FileNotFoundException("Un fichier sélectionné n’existe plus.", sourcePath);
                            }

                            var entry = archive.CreateEntry(
                                $"Fichiers/{index + 1:D5}-{fileName}",
                                CompressionLevel.Optimal);
                            await using var input = new FileStream(
                                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await using var entryOutput = entry.Open();
                            await input.CopyToAsync(entryOutput, 1024 * 1024, cancellationToken);
                            progress?.Report(index + 1);
                        }
                    }

                    encrypted.FlushFinalBlock();
                }

                await output.WriteAsync(hmac.GetHashAndReset(), cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, destinationPath);
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task<string> RestoreAsync(
        string backupPath,
        string destinationDirectory,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        await using var archiveStream = await DecryptVerifiedAsync(backupPath, password, cancellationToken);
        return await ExtractSafelyAsync(archiveStream, root, cancellationToken);
    }

    private static async Task<FileStream> DecryptVerifiedAsync(
        string backupPath,
        string password,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            backupPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length < HeaderLength + 16 + TagLength)
        {
            throw new InvalidDataException("Le fichier n’est pas une sauvegarde Zercraft valide.");
        }

        var header = new byte[HeaderLength];
        await ReadExactlyAsync(input, header, cancellationToken);
        var (salt, iv, iterations) = ParseHeader(header);
        if (iterations != Pbkdf2Iterations)
        {
            throw new InvalidDataException("Paramètres de chiffrement non pris en charge.");
        }

        var cipherLength = input.Length - HeaderLength - TagLength;
        if (cipherLength <= 0 || cipherLength % 16 != 0)
        {
            throw new InvalidDataException("Le contenu chiffré est tronqué ou invalide.");
        }

        var key = DeriveKey(password, salt);
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.AsSpan(32, 32).ToArray());
            hmac.AppendData(header);
            var buffer = new byte[1024 * 1024];
            long remaining = cipherLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("Sauvegarde chiffrée tronquée.");
                }

                hmac.AppendData(buffer, 0, read);
                remaining -= read;
            }

            var expectedTag = new byte[TagLength];
            await ReadExactlyAsync(input, expectedTag, cancellationToken);
            var actualTag = hmac.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expectedTag, actualTag))
            {
                throw new CryptographicException("Code incorrect ou sauvegarde modifiée : authentification refusée.");
            }

            input.Position = HeaderLength;
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key.AsSpan(0, 32).ToArray();
            aes.IV = iv;

            var tempPath = Path.Combine(Path.GetTempPath(), $"zercraft-{Guid.NewGuid():N}.tmp");
            var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            try
            {
                using (var crypto = new CryptoStream(output, aes.CreateDecryptor(), CryptoStreamMode.Write, leaveOpen: true))
                {
                    remaining = cipherLength;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var requested = (int)Math.Min(buffer.Length, remaining);
                        var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("Sauvegarde chiffrée tronquée.");
                        }

                        await crypto.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        remaining -= read;
                    }

                    crypto.FlushFinalBlock();
                }

                await output.FlushAsync(cancellationToken);
                output.Position = 0;
                return output;
            }
            catch
            {
                await output.DisposeAsync();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<string> ExtractSafelyAsync(
        Stream archiveStream,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("La sauvegarde contient trop d’éléments.");
        }

        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumRestoreBytes)
            {
                throw new InvalidDataException("La sauvegarde dépasse la limite de restauration de 100 Go.");
            }
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var root = Path.Combine(destinationRoot, $"Zercraft-Restauration-{timestamp}");
        var suffix = 1;
        while (Directory.Exists(root) || File.Exists(root))
        {
            root = Path.Combine(destinationRoot, $"Zercraft-Restauration-{timestamp}-{suffix++}");
        }

        Directory.CreateDirectory(root);
        try
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(normalized)
                    || normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "." or "..")
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new InvalidDataException("Chemin de fichier non sûr dans la sauvegarde.");
                }

                var target = Path.GetFullPath(Path.Combine(root, normalized));
                var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Chemin de fichier non sûr dans la sauvegarde.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(
                    target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
            }

            return root;
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static byte[] CreateHeader(byte[] salt, byte[] iv)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, sizeof(int)), Pbkdf2Iterations);
        salt.CopyTo(header, 12);
        iv.CopyTo(header, 12 + SaltLength);
        return header;
    }

    private static (byte[] Salt, byte[] Iv, int Iterations) ParseHeader(byte[] header)
    {
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Format de sauvegarde inconnu.");
        }

        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, sizeof(int)));
        var salt = header.AsSpan(12, SaltLength).ToArray();
        var iv = header.AsSpan(12 + SaltLength, IvLength).ToArray();
        return (salt, iv, iterations);
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 64);

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"Le code d’accès doit contenir au moins {MinimumPasswordLength} caractères.",
                nameof(password));
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Le fichier de sauvegarde est incomplet.");
            }

            offset += read;
        }
    }

    private sealed class AuthenticatedWriteStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            inner.Write(buffer);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            hash.AppendData(buffer, offset, count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
