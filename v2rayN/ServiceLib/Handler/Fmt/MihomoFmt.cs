using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ServiceLib.Handler.Fmt;

// Keep the original YAML nodes: translating them to URI fields loses plugin options.
public class MihomoFmt : BaseFmt
{
    public static List<ProfileItem> ResolveProxies(string data)
    {
        var root = ReadRoot(data);
        if (root == null || Get(root, "proxies") == null)
            return [];
        if (Get(root, "proxies") is not YamlSequenceNode proxies)
            throw new FormatException("Mihomo 订阅节点列表格式错误，未更新订阅。");

        if (proxies.Children.Any(p => p is not YamlMappingNode))
            throw new FormatException("Mihomo 订阅节点列表格式错误，未更新订阅。");
        var nodes = proxies.Children.OfType<YamlMappingNode>()
            .Where(p => Text(p, "type") is not ("direct" or "reject" or "dns" or "pass" or "compatible"))
            .ToList();
        if (nodes.Count == 0)
            throw new FormatException("Mihomo 订阅没有可导入的节点，未更新订阅。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(Text(node, "name")) ||
                string.IsNullOrWhiteSpace(Text(node, "type")) || !names.Add(Text(node, "name")) ||
                Text(node, "name") is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "COMPATIBLE" or "GLOBAL")
                throw new FormatException("Mihomo 订阅包含缺少名称、类型或名称重复的节点，未更新订阅。");
        }

        var payload = new YamlMappingNode();
        foreach (var key in new[] { "proxies", "proxy-groups", "proxy-providers", "dns", "hosts", "ipv6" })
            if (Get(root, key) is { } value) payload.Add(key, value);
        QuoteIdentifiers(payload);
        var yaml = WriteRoot(payload);
        List<ProfileItem> result = [];
        try
        {
            foreach (var node in nodes)
            {
                var name = Text(node, "name");
                int.TryParse(Text(node, "port"), out var port);
                var document = new MihomoNodeDocument(name, Text(node, "server"), port, yaml);
                result.Add(new ProfileItem
                {
                    ConfigType = EConfigType.Outbound,
                    CoreType = ECoreType.mihomo,
                    Remarks = name,
                    Port = port,
                    Address = WriteAllText(JsonUtils.Serialize(document)),
                });
            }
            return result;
        }
        catch
        {
            foreach (var item in result) File.Delete(item.Address);
            throw;
        }
    }

    internal static YamlMappingNode? ReadRoot(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new MergingParser(new Parser(new StringReader(yaml))));
            return stream.Documents.Count == 1 ? stream.Documents[0].RootNode as YamlMappingNode : null;
        }
        catch (YamlException)
        {
            // Parser exceptions can contain subscription passwords; never log the input.
            if (Regex.IsMatch(yaml, @"(?m)^\s*proxies\s*:"))
                throw new FormatException("Mihomo 订阅 YAML 格式错误，未更新订阅。");
            return null;
        }
    }

    internal static YamlNode? Get(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    internal static string Text(YamlMappingNode node, string key) =>
        (Get(node, key) as YamlScalarNode)?.Value ?? "";

    internal static string WriteRoot(YamlMappingNode root)
    {
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        return writer.ToString();
    }

    private static void QuoteIdentifiers(YamlNode node, HashSet<YamlNode>? visited = null)
    {
        visited ??= new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        if (!visited.Add(node)) return;
        if (node is YamlMappingNode map)
            foreach (var pair in map.Children)
            {
                if (((pair.Key as YamlScalarNode)?.Value is "short-id" or "password" or "uuid") && pair.Value is YamlScalarNode scalar)
                    scalar.Style = ScalarStyle.DoubleQuoted;
                QuoteIdentifiers(pair.Value, visited);
            }
        else if (node is YamlSequenceNode list)
            foreach (var child in list.Children) QuoteIdentifiers(child, visited);
    }

    internal static string BridgeConfig(MihomoNodeDocument document, int port)
    {
        var root = ReadRoot(document.Yaml) ?? throw new FormatException("Mihomo 节点配置无效。");
        // Only retain the selected node and its dialer dependencies. Unrelated subscription
        // groups must not start health checks or download providers for every tested node.
        var all = (Get(root, "proxies") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>().ToList() ?? [];
        var groups = (Get(root, "proxy-groups") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>().ToList() ?? [];
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var usedProviders = new HashSet<string>(StringComparer.Ordinal);
        var builtins = new HashSet<string>(["DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE"], StringComparer.Ordinal);
        void Include(string name)
        {
            if (builtins.Contains(name) || !needed.Add(name)) return;
            var proxy = all.FirstOrDefault(p => Text(p, "name") == name);
            if (proxy != null)
            {
                var dialer = Text(proxy, "dialer-proxy");
                if (dialer.Length > 0) Include(dialer);
                return;
            }
            var group = groups.FirstOrDefault(p => Text(p, "name") == name)
                ?? throw new FormatException("Mihomo 节点引用的代理或策略组不存在。");
            if (Get(group, "proxies") is YamlSequenceNode children)
                foreach (var child in children.Children.OfType<YamlScalarNode>()) Include(child.Value ?? "");
            if (Get(group, "use") is YamlSequenceNode providers)
                foreach (var child in providers.Children.OfType<YamlScalarNode>()) usedProviders.Add(child.Value ?? "");
            if (Text(group, "include-all") == "true" || Text(group, "include-all-proxies") == "true")
                foreach (var child in all) Include(Text(child, "name"));
            if ((Text(group, "include-all") == "true" || Text(group, "include-all-providers") == "true") &&
                Get(root, "proxy-providers") is YamlMappingNode providerMap)
                foreach (var key in providerMap.Children.Keys.OfType<YamlScalarNode>()) usedProviders.Add(key.Value ?? "");
        }
        Include(document.Name);
        root.Children[new YamlScalarNode("proxies")] = new YamlSequenceNode(all.Where(p => needed.Contains(Text(p, "name"))));
        root.Children[new YamlScalarNode("proxy-groups")] = new YamlSequenceNode(groups.Where(p => needed.Contains(Text(p, "name"))));
        if (Get(root, "proxy-providers") is YamlMappingNode providerNodes)
        {
            foreach (var key in providerNodes.Children.Keys.ToList())
                if (!usedProviders.Contains((key as YamlScalarNode)?.Value ?? "")) providerNodes.Children.Remove(key);
        }
        if (Get(root, "dns") is YamlMappingNode dns)
        {
            dns.Children.Remove(new YamlScalarNode("listen"));
            dns.Children[new YamlScalarNode("enhanced-mode")] = new YamlScalarNode("redir-host");
            dns.Children[new YamlScalarNode("respect-rules")] = new YamlScalarNode("false");
        }
        root.Children[new YamlScalarNode("mode")] = new YamlScalarNode("rule");
        root.Children[new YamlScalarNode("log-level")] = new YamlScalarNode("silent");
        root.Children[new YamlScalarNode("listeners")] = new YamlSequenceNode(new YamlMappingNode
        {
            { "name", "v2rayn-bridge" }, { "type", "mixed" }, { "listen", Global.Loopback },
            { "port", port.ToString() }, { "udp", "true" }, { "proxy", document.Name },
        });
        root.Children[new YamlScalarNode("rules")] = new YamlSequenceNode(new YamlScalarNode("MATCH,REJECT"));
        return WriteRoot(root);
    }
}

public sealed record MihomoNodeDocument(string Name, string Server, int Port, string Yaml);
