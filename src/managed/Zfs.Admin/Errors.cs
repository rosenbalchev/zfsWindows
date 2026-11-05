namespace Zfs.Admin;

/// <summary>
/// Error categories shared by the broker, zfswin.exe and the PowerShell
/// module.  The wire names and the CLI exit codes are a stable contract
/// (docs/zfswin-cli.md); add new values, never renumber.
/// </summary>
public enum ErrorCode
{
    CommandFailed,
    Usage,
    InvalidArgument,
    NotFound,
    AccessDenied,
    BrokerUnavailable,
    ConfirmationRequired,
    Timeout,
    Cancelled,
    ProtocolMismatch,
    BadRequest,
    Busy,
    Internal,
}

public static class ErrorCodes
{
    static readonly (ErrorCode Code, string Wire, int Exit)[] Table =
    [
        (ErrorCode.CommandFailed, "command-failed", 1),
        (ErrorCode.Usage, "usage", 2),
        (ErrorCode.InvalidArgument, "invalid-argument", 2),
        (ErrorCode.NotFound, "not-found", 3),
        (ErrorCode.AccessDenied, "access-denied", 4),
        (ErrorCode.BrokerUnavailable, "broker-unavailable", 5),
        (ErrorCode.ConfirmationRequired, "confirmation-required", 6),
        (ErrorCode.Timeout, "timeout", 7),
        (ErrorCode.Cancelled, "cancelled", 7),
        (ErrorCode.ProtocolMismatch, "protocol-mismatch", 8),
        (ErrorCode.BadRequest, "bad-request", 8),
        (ErrorCode.Busy, "busy", 9),
        (ErrorCode.Internal, "internal", 10),
    ];

    public static string ToWire(this ErrorCode code) => Table.First(t => t.Code == code).Wire;

    public static int ExitCode(this ErrorCode code) => Table.First(t => t.Code == code).Exit;

    public static ErrorCode FromWire(string? wire) =>
        Table.FirstOrDefault(t => t.Wire == wire) is { Wire: not null } t ? t.Code : ErrorCode.Internal;
}

public sealed class AdminException(ErrorCode code, string message, string? detail = null) : Exception(message)
{
    public ErrorCode Code { get; } = code;
    public string? Detail { get; } = detail;
}
