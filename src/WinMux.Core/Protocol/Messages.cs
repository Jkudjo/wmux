using System.Text.Json.Serialization;

namespace WinMux.Core.Protocol;

// Base types
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateSessionRequest), typeDiscriminator: "CreateSession")]
[JsonDerivedType(typeof(AttachRequest), typeDiscriminator: "Attach")]
[JsonDerivedType(typeof(InputRequest), typeDiscriminator: "Input")]
[JsonDerivedType(typeof(ResizeRequest), typeDiscriminator: "Resize")]
[JsonDerivedType(typeof(ListRequest), typeDiscriminator: "List")]
[JsonDerivedType(typeof(KillRequest), typeDiscriminator: "Kill")]
[JsonDerivedType(typeof(DetachRequest), typeDiscriminator: "Detach")]
[JsonDerivedType(typeof(PingRequest), typeDiscriminator: "Ping")]
public abstract record RequestMessage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OutputEvent), typeDiscriminator: "Output")]
[JsonDerivedType(typeof(ExitEvent), typeDiscriminator: "Exit")]
[JsonDerivedType(typeof(AckEvent), typeDiscriminator: "Ack")]
[JsonDerivedType(typeof(ErrorEvent), typeDiscriminator: "Error")]
[JsonDerivedType(typeof(SessionsEvent), typeDiscriminator: "Sessions")]
[JsonDerivedType(typeof(PongEvent), typeDiscriminator: "Pong")]
[JsonDerivedType(typeof(CreatedEvent), typeDiscriminator: "Created")]
[JsonDerivedType(typeof(AttachedEvent), typeDiscriminator: "Attached")]
public abstract record EventMessage;

// Requests
public sealed record CreateSessionRequest(
    string Name,
    string? Shell = null,
    string? Cwd = null,
    Dictionary<string, string>? Env = null,
    int? Cols = null,
    int? Rows = null
) : RequestMessage;

public sealed record AttachRequest(string IdOrName) : RequestMessage;

public sealed record InputRequest(string SessionId, byte[] Data) : RequestMessage;

public sealed record ResizeRequest(string SessionId, int Cols, int Rows) : RequestMessage;

public sealed record ListRequest() : RequestMessage;

public sealed record KillRequest(string SessionId) : RequestMessage;

public sealed record DetachRequest(string SessionId) : RequestMessage;

public sealed record PingRequest() : RequestMessage;

// Events
public sealed record OutputEvent(string SessionId, byte[] Data) : EventMessage;

public sealed record ExitEvent(string SessionId, int Code) : EventMessage;

public sealed record AckEvent(int ReqId) : EventMessage;

public sealed record ErrorEvent(int? ReqId, string Code, string Message) : EventMessage;

public sealed record SessionsEvent(IReadOnlyList<SessionSummary> Sessions) : EventMessage;

public sealed record PongEvent(DateTimeOffset ServerTime) : EventMessage;

public sealed record CreatedEvent(string SessionId) : EventMessage;

public sealed record AttachedEvent(string SessionId) : EventMessage;

// Models
public sealed record SessionSummary(
    string Id,
    string Name,
    string State,
    int Cols,
    int Rows,
    string Shell,
    string Cwd,
    int ShellPid,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt
);
