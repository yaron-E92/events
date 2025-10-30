using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Yaref92.Events.Transports;

public static class SessionFrameIO
{
    public static async Task<FrameReadResult> ReadFrameAsync(NetworkStream stream, byte[] lengthBuffer, CancellationToken cancellationToken)
    {
        var lengthStatus = await TryReadAsync(stream, lengthBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (!lengthStatus)
        {
            return FrameReadResult.Failed();
        }

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0)
        {
            return FrameReadResult.Failed();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var payloadStatus = await TryReadAsync(stream, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            if (!payloadStatus)
            {
                return FrameReadResult.Failed();
            }

            var json = Encoding.UTF8.GetString(buffer, 0, length);
            var frame = JsonSerializer.Deserialize<SessionFrame>(json, SessionFrameSerializer.Options);
            return frame is null
                ? FrameReadResult.Failed()
                : FrameReadResult.Success(frame);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> TryReadAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.Slice(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    public readonly record struct FrameReadResult(bool IsSuccess, SessionFrame? Frame)
    {
        public static FrameReadResult Success(SessionFrame frame) => new(true, frame);

        public static FrameReadResult Failed() => new(false, null);
    }
}
