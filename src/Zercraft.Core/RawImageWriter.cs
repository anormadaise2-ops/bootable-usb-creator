namespace Zercraft.Core;

public static class RawImageWriter
{
    public const int BufferSize = 1024 * 1024;

    public static async Task<long> WriteAsync(
        Stream source,
        Stream destination,
        long imageSize,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (!source.CanRead)
        {
            throw new ArgumentException("Le fichier ISO ne peut pas être lu.", nameof(source));
        }

        if (!destination.CanWrite)
        {
            throw new ArgumentException("Le périphérique USB ne peut pas être écrit.", nameof(destination));
        }

        if (imageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageSize), "L’image doit contenir au moins un octet.");
        }

        var buffer = new byte[BufferSize];
        long written = 0;
        while (written < imageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, imageSize - written);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Le fichier image a été tronqué pendant l’écriture.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            progress?.Report(written);
        }

        await destination.FlushAsync(cancellationToken);
        return written;
    }
}
