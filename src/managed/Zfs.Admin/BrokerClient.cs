using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Zfs.Admin;

public sealed class BrokerClient(string pipeName = Protocol.PipeName)
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends one request and returns its result.  Cancelling the token closes
    /// the connection, which makes the broker cancel the operation.
    /// </summary>
    public async Task<JsonNode?> CallAsync(string operation, JsonObject? arguments = null, CancellationToken cancellationToken = default)
    {
        var request = new BrokerRequest
        {
            Protocol = Protocol.Version,
            Id = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Arguments = arguments ?? new JsonObject(),
        };
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new AdminException(ErrorCode.BrokerUnavailable,
                "The OpenZFS broker service is not running (net start ZfsWinBroker), or it is busy.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new AdminException(ErrorCode.AccessDenied, "Access to the OpenZFS broker service was denied.");
        }

        byte[]? payload;
        try
        {
            await Protocol.WriteFrameAsync(pipe, request.Serialize(), Protocol.MaxRequestBytes, cancellationToken).ConfigureAwait(false);
            payload = await Protocol.ReadFrameAsync(pipe, Protocol.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new AdminException(ErrorCode.Cancelled, $"'{operation}' was cancelled.");
        }
        catch (IOException e)
        {
            throw new AdminException(ErrorCode.BrokerUnavailable, $"The connection to the OpenZFS broker failed: {e.Message}");
        }
        if (payload is null)
            throw new AdminException(ErrorCode.BrokerUnavailable, "The OpenZFS broker closed the connection without answering.");

        BrokerResponse response = BrokerResponse.Parse(payload);
        if (response.Protocol != Protocol.Version)
            throw new AdminException(ErrorCode.ProtocolMismatch, $"The broker answered with protocol {response.Protocol}; this tool speaks {Protocol.Version}.");
        if (response.Id.Length > 0 && response.Id != request.Id)
            throw new AdminException(ErrorCode.ProtocolMismatch, "The broker answered a different request.");
        if (!response.Ok)
            throw new AdminException(ErrorCodes.FromWire(response.Error?.Code), response.Error?.Message ?? "The broker reported an unspecified error.", response.Error?.Detail);
        return response.Result;
    }
}
