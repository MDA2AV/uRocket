namespace URocket.Engine.Configs;

public sealed record AcceptorConfig(
    //uint RingFlags = ABI.ABI.IORING_SETUP_SQPOLL | ABI.ABI.IORING_SETUP_SQ_AFF,
    uint RingFlags = 0,
    int SqCpuThread = -1,
    uint SqThreadIdleMs = 100,
    uint RingEntries = 512,
    uint BatchSqes = 4096);
