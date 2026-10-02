using System.Text.Json.Serialization;

namespace DshSessionHud;

// 说明：这些类型逐字段对应 lib/index.js 写出的快照 JSON。
// 插件侧字段缺失时一律回落到默认值，保证窗口永不因为 schema 漂移而崩。

public sealed class StateSnapshot
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("generatedAt")] public long GeneratedAt { get; set; }
    [JsonPropertyName("host")] public HostInfo? Host { get; set; }
    [JsonPropertyName("totals")] public TotalsInfo? Totals { get; set; }
    [JsonPropertyName("approvals")] public List<ApprovalState> Approvals { get; set; } = new();
    [JsonPropertyName("sessions")] public List<SessionState> Sessions { get; set; } = new();
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class HostInfo
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("dshHome")] public string? DshHome { get; set; }
    [JsonPropertyName("stateFile")] public string? StateFile { get; set; }
    [JsonPropertyName("stopped")] public bool Stopped { get; set; }
}

public sealed class TotalsInfo
{
    [JsonPropertyName("sessions")] public int Sessions { get; set; }
    [JsonPropertyName("running")] public int Running { get; set; }
    [JsonPropertyName("pendingApprovals")] public int PendingApprovals { get; set; }
}

public sealed class ApprovalState
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("sessionTitle")] public string? SessionTitle { get; set; }
    [JsonPropertyName("workspace")] public string? Workspace { get; set; }
    [JsonPropertyName("toolName")] public string? ToolName { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("displayReason")] public string? DisplayReason { get; set; }
    [JsonPropertyName("callId")] public string? CallId { get; set; }
    [JsonPropertyName("askedAt")] public long AskedAt { get; set; }
}

public sealed class SessionState
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("shortId")] public string? ShortId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("topic")] public string? Topic { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("workspace")] public string? Workspace { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("parentId")] public string? ParentId { get; set; }
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("turn")] public int Turn { get; set; }
    [JsonPropertyName("step")] public int Step { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("approvalPolicy")] public string? ApprovalPolicy { get; set; }
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    [JsonPropertyName("turnStartedAt")] public long TurnStartedAt { get; set; }
    [JsonPropertyName("stepStartedAt")] public long StepStartedAt { get; set; }
    [JsonPropertyName("idleMs")] public long IdleMs { get; set; }
    [JsonPropertyName("current")] public CurrentWork? Current { get; set; }
    [JsonPropertyName("lastText")] public string? LastText { get; set; }
    [JsonPropertyName("usage")] public UsageState? Usage { get; set; }
    [JsonPropertyName("tools")] public List<ToolEntry> Tools { get; set; } = new();
    [JsonPropertyName("pendingApproval")] public ApprovalState? PendingApproval { get; set; }
    [JsonPropertyName("lastOutcome")] public OutcomeState? LastOutcome { get; set; }
}

public sealed class CurrentWork
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("since")] public long Since { get; set; }
    [JsonPropertyName("callId")] public string? CallId { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
}

public sealed class UsageState
{
    [JsonPropertyName("input")] public long Input { get; set; }
    [JsonPropertyName("output")] public long Output { get; set; }
    [JsonPropertyName("total")] public long Total { get; set; }
}

public sealed class ToolEntry
{
    [JsonPropertyName("callId")] public string? CallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("at")] public long At { get; set; }
    [JsonPropertyName("endedAt")] public long EndedAt { get; set; }
    [JsonPropertyName("ms")] public long Ms { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class OutcomeState
{
    [JsonPropertyName("toolName")] public string? ToolName { get; set; }
    [JsonPropertyName("outcome")] public string? Outcome { get; set; }
    [JsonPropertyName("at")] public long At { get; set; }
}
