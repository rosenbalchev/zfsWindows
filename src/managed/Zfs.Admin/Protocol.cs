using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Zfs.Admin;

/// <summary>
/// The broker's local IPC protocol: one request and one response per pipe
/// connection, each a 4-byte little-endian length followed by that many bytes
/// of UTF-8 JSON.  Both sides bound the frame size before allocating, and a
/// request names the protocol version it speaks.
/// </summary>
public static class Protocol
{
    public const int Version = 1;
    public const string PipeName = "zfswin-broker";
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxResponseBytes = 16 * 1024 * 1024;


    public static async Task WriteFrameAsync(Stream stream, byte[] payload, int maxBytes, CancellationToken cancellationToken)
    {
        if (payload.Length > maxBytes)
            throw new AdminException(ErrorCode.BadRequest, $"Message of {payload.Length} bytes exceeds the {maxBytes}-byte limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; returns null on a clean end of stream before the header.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        int read = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
            return null;
        if (read < header.Length)
            throw new AdminException(ErrorCode.BadRequest, "Truncated message header.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxBytes)
            throw new AdminException(ErrorCode.BadRequest, $"Message length {length} is outside 0..{maxBytes}.");
        var payload = new byte[length];
        if (await ReadAtLeastAsync(stream, payload, cancellationToken).ConfigureAwait(false) != length)
            throw new AdminException(ErrorCode.BadRequest, "Truncated message body.");
        return payload;
    }

    static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }
}

public sealed class BrokerRequest
{
    public int Protocol { get; set; }
    public string Id { get; set; } = "";
    public string Operation { get; set; } = "";
    public JsonObject Arguments { get; set; } = new();

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, ProtocolJson.Default.BrokerRequest);

    public static BrokerRequest Parse(byte[] payload)
    {
        BrokerRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(payload, ProtocolJson.Default.BrokerRequest);
        }
        catch (JsonException e)
        {
            throw new AdminException(ErrorCode.BadRequest, $"Malformed request: {e.Message}");
        }
        if (request is null)
            throw new AdminException(ErrorCode.BadRequest, "Empty request.");
        request.Arguments ??= new JsonObject();
        if (request.Id.Length > 64)
            throw new AdminException(ErrorCode.BadRequest, "Request id is longer than 64 characters.");
        return request;
    }
}

public sealed class BrokerResponse
{
    public int Protocol { get; set; } = Admin.Protocol.Version;
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public JsonNode? Result { get; set; }
    public BrokerError? Error { get; set; }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, ProtocolJson.Default.BrokerResponse);

    public static BrokerResponse Parse(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, ProtocolJson.Default.BrokerResponse)
                ?? throw new AdminException(ErrorCode.ProtocolMismatch, "Empty response from the broker.");
        }
        catch (JsonException e)
        {
            throw new AdminException(ErrorCode.ProtocolMismatch, $"Malformed response from the broker: {e.Message}");
        }
    }

    public static BrokerResponse Failure(string id, AdminException e) => new()
    {
        Id = id,
        Ok = false,
        Error = new BrokerError { Code = e.Code.ToWire(), Message = e.Message, Detail = e.Detail },
    };
}

public sealed class BrokerError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>The ZFS command's diagnostic output, when one failed.</summary>
    public string? Detail { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 64)]
[JsonSerializable(typeof(BrokerRequest))]
[JsonSerializable(typeof(BrokerResponse))]
internal sealed partial class ProtocolJson : JsonSerializerContext;
