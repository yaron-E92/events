using System.Buffers;
using System.Net.Sockets;
using System.Text.Json;

namespace Yaref92.Events.Sessions;

public static class SessionFrameIO
{
    public static async Task<FrameReadResult> ReadFrameAsync(
        NetworkStream stream,
        byte[] lengthBuffer,
        CancellationToken cancellationToken,
        int maxFrameBytes = ResilientSessionOptions.DefaultMaxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(lengthBuffer);
        if (lengthBuffer.Length < sizeof(int))
        {
            throw new ArgumentException("The frame length buffer must contain at least four bytes.", nameof(lengthBuffer));
        }
        if (maxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        }

        var lengthStatus = await TryReadAsync(stream, lengthBuffer.AsMemory(0, sizeof(int)), cancellationToken).ConfigureAwait(false);
        if (!lengthStatus)
        {
            return FrameReadResult.Failed(FrameReadFailure.IncompleteFrame);
        }

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0)
        {
            return FrameReadResult.Failed(FrameReadFailure.InvalidLength);
        }
        if (length > maxFrameBytes)
        {
            return FrameReadResult.Failed(FrameReadFailure.FrameTooLarge);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var payloadStatus = await TryReadAsync(stream, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            if (!payloadStatus)
            {
                return FrameReadResult.Failed(FrameReadFailure.IncompleteFrame);
            }

            SessionFrame? frame;
            try
            {
                frame = JsonSerializer.Deserialize<SessionFrame>(buffer.AsSpan(0, length), SessionFrameSerializer.Options);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return FrameReadResult.Failed(FrameReadFailure.MalformedPayload);
            }

            return frame is not null && IsValidFrame(frame)
                ? FrameReadResult.Success(frame)
                : FrameReadResult.Failed(FrameReadFailure.InvalidFrame);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsValidFrame(SessionFrame frame)
    {
        return frame.Kind switch
        {
            SessionFrameKind.Auth => frame.Id == Guid.Empty && !string.IsNullOrWhiteSpace(frame.Token),
            SessionFrameKind.Event => frame.Id != Guid.Empty && frame.Token is null && frame.Payload is not null,
            SessionFrameKind.Ack => frame.Id != Guid.Empty && frame.Token is null && frame.Payload is null,
            SessionFrameKind.Ping or SessionFrameKind.Pong => frame.Id == Guid.Empty && frame.Token is null && frame.Payload is null,
            _ => false,
        };
    }

    private static async Task<bool> TryReadAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    public enum FrameReadFailure
    {
        None,
        IncompleteFrame,
        InvalidLength,
        FrameTooLarge,
        MalformedPayload,
        InvalidFrame,
    }

    public readonly record struct FrameReadResult(bool IsSuccess, SessionFrame? Frame, FrameReadFailure Failure)
    {
        public static FrameReadResult Success(SessionFrame frame) => new(true, frame, FrameReadFailure.None);

        public static FrameReadResult Failed(FrameReadFailure failure) => new(false, null, failure);
    }
}
