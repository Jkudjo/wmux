using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace WinMux.Core.Protocol;

public static class Serialization
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteFramedAsync<T>(Stream stream, T message, CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadFramedAsync<T>(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        int read = await FillBufferAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0) return default; // disconnected
        if (read < 4) throw new IOException("Unexpected EOF while reading frame header.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > 64 * 1024 * 1024) throw new IOException("Invalid frame length.");
        byte[] payload = new byte[length];
        read = await FillBufferAsync(stream, payload, ct).ConfigureAwait(false);
        if (read < length) throw new IOException("Unexpected EOF while reading frame payload.");
        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    private static async Task<int> FillBufferAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (n == 0) return offset; // EOF
            offset += n;
        }
        return offset;
    }
}

