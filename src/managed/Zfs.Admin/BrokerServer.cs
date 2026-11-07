using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Zfs.Admin;

/// <summary>Who is calling, as established from the pipe client's token.</summary>
public sealed record CallerIdentity(string Name, bool IsAdministrator);

public interface ICallerAuthenticator
{
    CallerIdentity Identify(NamedPipeServerStream pipe);
}

/// <summary>
/// Reads the caller's identity by impersonating the pipe client.  Under UAC
/// a non-elevated administrator's token has Administrators as deny-only, so
/// IsInRole is false and mutations are refused until the caller elevates.
/// </summary>
public sealed class WindowsCallerAuthenticator : ICallerAuthenticator
{
    public CallerIdentity Identify(NamedPipeServerStream pipe)
    {
        CallerIdentity? identity = null;
        pipe.RunAsClient(() =>
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            identity = new CallerIdentity(current.Name, new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator));
        });
        return identity ?? throw new AdminException(ErrorCode.AccessDenied, "The caller could not be identified.");
    }
}

public sealed record AuditEntry(string Caller, string Operation, JsonObject Arguments, bool Ok, string? ErrorCode, TimeSpan Elapsed);

public sealed class BrokerOptions
{
    public string PipeName { get; init; } = Protocol.PipeName;
    /// <summary>Concurrent connections; further clients wait in the pipe's backlog.</summary>
    public int MaxConnections { get; init; } = 8;
    /// <summary>A client that does not send its request within this time is dropped.</summary>
    public TimeSpan RequestReadTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Restrict the pipe to SYSTEM, Administrators and interactive/service local users.</summary>
    public bool SecurePipe { get; init; } = true;
}

/// <summary>
/// The broker: the single place that runs privileged ZFS commands.  One
/// request per connection.  Read-only operations run concurrently; mutations
/// run one at a time.  A client disconnect cancels its operation.
/// </summary>
public sealed class BrokerServer(IToolRunner runner, ICallerAuthenticator authenticator, BrokerOptions options, Action<AuditEntry>? audit = null)
{
    readonly SemaphoreSlim mutationLock = new(1, 1);

    public async Task RunAsync(CancellationToken stopping)
    {
        var workers = Enumerable.Range(0, options.MaxConnections).Select(_ => ServeLoopAsync(stopping)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    async Task ServeLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100, stopping).ConfigureAwait(false);
                continue;
            }
            await using (pipe.ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(pipe, stopping).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // Client went away; nothing to answer.
                }
            }
        }
    }

    NamedPipeServerStream CreatePipe()
    {
        const PipeOptions pipeOptions = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
        if (!options.SecurePipe)
            return new NamedPipeServerStream(options.PipeName, PipeDirection.InOut, options.MaxConnections, PipeTransmissionMode.Byte, pipeOptions);
        var security = new PipeSecurity();
        // The account running the broker (SYSTEM for the service) creates
        // every pipe instance, which needs more than read/write.
        using (WindowsIdentity self = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Local callers only.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(options.PipeName, PipeDirection.InOut, options.MaxConnections,
            PipeTransmissionMode.Byte, pipeOptions, Protocol.MaxRequestBytes, 64 * 1024, security);
    }

    async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        BrokerResponse response;
        string id = "";
        using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        readLimit.CancelAfter(options.RequestReadTimeout);
        byte[]? payload;
        try
        {
            payload = await Protocol.ReadFrameAsync(pipe, Protocol.MaxRequestBytes, readLimit.Token).ConfigureAwait(false);
        }
        catch (AdminException e)
        {
            await SendAsync(pipe, BrokerResponse.Failure("", e), stopping).ConfigureAwait(false);
            return;
        }
        if (payload is null)
            return;

        // The client signals cancellation by closing its end: a pending read
        // completes (0 bytes or an error) and cancels the running operation.
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _ = WatchDisconnectAsync(pipe, operation);
        try
        {
            BrokerRequest request = BrokerRequest.Parse(payload);
            id = request.Id;
            response = await DispatchAsync(pipe, request, operation.Token).ConfigureAwait(false);
        }
        catch (AdminException e)
        {
            response = BrokerResponse.Failure(id, e);
        }
        if (!operation.IsCancellationRequested || stopping.IsCancellationRequested)
            await SendAsync(pipe, response, stopping).ConfigureAwait(false);
    }

    static async Task WatchDisconnectAsync(NamedPipeServerStream pipe, CancellationTokenSource operation)
    {
        try
        {
            var probe = new byte[1];
            await pipe.ReadAsync(probe, operation.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        try
        {
            operation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Runs an operation as the broker's own identity, on every thread its
    /// awaits continue on.  In the Native AOT broker, continuations after a
    /// tool run were seen on thread-pool threads still impersonating a pipe
    /// client at Identification level, where every file access fails with
    /// ERROR_BAD_IMPERSONATION_LEVEL (zpool.exe then looked "missing").  An
    /// invalid token means "no impersonation" for the whole async flow.
    /// </summary>
    static Task<JsonNode?> AsBroker(Func<Task<JsonNode?>> execute) =>
        WindowsIdentity.RunImpersonatedAsync(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle.InvalidHandle, execute);

    async Task<BrokerResponse> DispatchAsync(NamedPipeServerStream pipe, BrokerRequest request, CancellationToken cancellationToken)
    {
        if (request.Protocol != Protocol.Version)
            throw new AdminException(ErrorCode.ProtocolMismatch, $"The broker speaks protocol {Protocol.Version}; the request used {request.Protocol}.");
        if (!Operations.All.TryGetValue(request.Operation, out OperationSpec? spec))
            throw new AdminException(ErrorCode.BadRequest, $"Unknown operation '{request.Operation}'.");

        CallerIdentity caller = authenticator.Identify(pipe);
        var arguments = (JsonObject)request.Arguments.DeepClone();
        bool confirmed = Validation.Flag(arguments, "confirm");
        arguments.Remove("confirm");
        Validation.RejectUnknown(arguments, spec.Arguments);

        var started = DateTime.UtcNow;
        try
        {
            if (spec.Privileged && !caller.IsAdministrator)
                throw new AdminException(ErrorCode.AccessDenied, $"'{spec.Name}' requires an elevated administrator.");
            if (spec.Destructive && !confirmed)
                throw new AdminException(ErrorCode.ConfirmationRequired, $"'{spec.Name}' changes or destroys data and must be confirmed.");

            var context = new OperationContext(runner, cancellationToken);
            JsonNode? result;
            if (spec.Privileged)
            {
                if (!await mutationLock.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false))
                    throw new AdminException(ErrorCode.Busy, "Another administrative operation is still running.");
                try
                {
                    result = await AsBroker(() => spec.Execute(context, arguments)).ConfigureAwait(false);
                }
                finally
                {
                    mutationLock.Release();
                }
            }
            else
            {
                result = await AsBroker(() => spec.Execute(context, arguments)).ConfigureAwait(false);
            }
            if (spec.Privileged)
                audit?.Invoke(new AuditEntry(caller.Name, spec.Name, arguments, true, null, DateTime.UtcNow - started));
            return new BrokerResponse { Id = request.Id, Ok = true, Result = result };
        }
        catch (OperationCanceledException)
        {
            var e = new AdminException(ErrorCode.Cancelled, $"'{spec.Name}' was cancelled.");
            if (spec.Privileged)
                audit?.Invoke(new AuditEntry(caller.Name, spec.Name, arguments, false, e.Code.ToWire(), DateTime.UtcNow - started));
            throw e;
        }
        catch (AdminException e)
        {
            if (spec.Privileged)
                audit?.Invoke(new AuditEntry(caller.Name, spec.Name, arguments, false, e.Code.ToWire(), DateTime.UtcNow - started));
            throw;
        }
    }

    static async Task SendAsync(Stream pipe, BrokerResponse response, CancellationToken cancellationToken)
    {
        byte[] bytes = response.Serialize();
        if (bytes.Length > Protocol.MaxResponseBytes)
            bytes = BrokerResponse.Failure(response.Id, new AdminException(ErrorCode.Internal, "The result is larger than the protocol allows.")).Serialize();
        await Protocol.WriteFrameAsync(pipe, bytes, Protocol.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
    }
}
