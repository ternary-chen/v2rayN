namespace AdaptiveRouting;

public sealed class LearningOptions
{
    public bool Enabled { get; set; }
    public int ListenerPort { get; set; } = 17880;
    public int ProxyPort { get; set; } = 17881;
    public int DirectTimeoutMs { get; set; } = 2500;
    public int ProxyTimeoutMs { get; set; } = 8000;
    public int LifetimeHours { get; set; } = 24;
    public int MaxEntries { get; set; } = 2000;
    public List<string> DirectProcesses { get; set; } =
    [
        "AweSun", "OldAweSun", "awesun_guard", "SunloginClient", "SunloginClient_Desktop",
        "GameViewer", "GameViewerService", "GameViewerServer", "GameViewerHealthd",
        "GameViewerLauncher", "uuyc-cli", "uuyc-mux",
        "NTELauncher", "NTEGame", "NTEBrowser", "NTEWebBooster", "NTEUpdate", "NTEErrRep", "HTGame"
    ];

    public void Validate()
    {
        if (ListenerPort is < 1024 or > 65535 || ProxyPort is < 1024 or > 65535 || ListenerPort == ProxyPort)
            throw new ArgumentException("学习端口必须在 1024–65535 之间，且不能相同。");
        if (DirectTimeoutMs is < 500 or > 10000 || ProxyTimeoutMs is < 1000 or > 30000)
            throw new ArgumentException("直连检测限时应为 500–10000 ms，代理检测限时应为 1000–30000 ms。");
        if (LifetimeHours is < 1 or > 168 || MaxEntries is < 1 or > 10000)
            throw new ArgumentException("学习记录有效期应为 1–168 小时，容量应为 1–10000 条。");
        if (DirectProcesses.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1024))
            throw new ArgumentException("强制直连进程不能为空。");
    }
}
