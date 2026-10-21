using System.Text.Json;

namespace AdaptiveRouting;

public static class AiDomainCatalog
{
    private static readonly JsonElement Data = Load();
    private static JsonElement Load()
    {
        using var stream = typeof(AiDomainCatalog).Assembly.GetManifestResourceStream("AdaptiveRouting.ai-catalog.json")
            ?? throw new InvalidOperationException("缺少 AI 域名目录。");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
    public static string Version => Data.GetProperty("version").GetString()!;
    public static IReadOnlyList<string> Categories => Data.GetProperty("categories").EnumerateArray().Select(x => x.GetString()!).ToArray();
    public static IReadOnlyList<string> Supplements => Data.GetProperty("supplementDomains").EnumerateArray().Select(x => x.GetString()!).ToArray();
    public static IReadOnlyList<string> Domains => Categories.Select(x => "geosite:" + x).Concat(Supplements).Distinct().ToArray();
    public static string Summary => $"AI 名单 {Version}：{Categories.Count} 个社区厂商分类 + {Supplements.Count} 条本地补充。社区域名随 geosite.dat 更新，节点选择保持不变。";
}
