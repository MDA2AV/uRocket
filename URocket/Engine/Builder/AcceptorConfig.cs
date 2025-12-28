namespace URocket.Engine.Builder;

public sealed record AcceptorConfig(
    uint RingFlags = 0,
    int SqCpuThread = -1,
    uint SqThreadIdleMs = 100,
    uint RingEntries = 512);
