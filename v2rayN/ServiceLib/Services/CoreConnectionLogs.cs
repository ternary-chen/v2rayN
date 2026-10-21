using AdaptiveRouting;

namespace ServiceLib.Services;

public static class CoreConnectionLogs
{
    public static ConnectionLogFormatter Formatter(CoreConfigContext context, string? generatedJson = null)
    {
        // Groups/chains cannot be described as one actual remote node.
        var defaultName = context.Node.ConfigType.IsComplexType()
            ? "未知（组或链式出口，需查看核心标签）" : context.Node.Remarks ?? "未命名节点";
        return new(defaultName, context.AiPolicyNode?.Remarks,
            generatedJson is null ? new Dictionary<string, int>() : ConnectionLogFormatter.ReadInboundPorts(generatedJson));
    }
}
