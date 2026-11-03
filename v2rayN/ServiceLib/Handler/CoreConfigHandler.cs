namespace ServiceLib.Handler;

/// <summary>
/// Core configuration file processing class
/// </summary>
public static class CoreConfigHandler
{
    private static readonly string _tag = "CoreConfigHandler";

    public static async Task<RetResult> GenerateClientConfig(CoreConfigContext context, string? fileName)
    {
        var config = AppManager.Instance.Config;
        var result = new RetResult();
        var node = context.Node;

        if (node.ConfigType == EConfigType.Custom)
        {
            result = node.CoreType switch
            {
                ECoreType.mihomo => await new CoreConfigClashService(config, context.IsTunEnabled).GenerateClientCustomConfig(node, fileName),
                _ => await GenerateClientCustomConfig(node, fileName)
            };
        }
        else if (context.RunCoreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        }
        else
        {
            result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        }
        if (result.Success != true)
        {
            return result;
        }
        if (context.RunCoreType == ECoreType.Xray && node.ConfigType != EConfigType.Custom && result.Data != null)
        {
            try
            {
                if (context.ApplyCommunityPolicy)
                    result.Data = AdaptiveRouting.XrayCommunityPolicyConfig.Apply(result.Data.ToString(), context.AppConfig.LimitedLearning?.Enabled == true);
                if (context.ApplyTrafficPolicies)
                    result.Data = AdaptiveRouting.XrayAiPolicyConfig.Apply(result.Data.ToString());
                if (context.ApplyManualRules)
                    result.Data = AdaptiveRouting.XrayManualRoutingConfig.Apply(result.Data.ToString(), context.AppConfig.ManualRules);
                var options = context.AppConfig.LimitedLearning ?? new();
                result.Data = AdaptiveRouting.XrayLearningConfig.Apply(result.Data.ToString(), options, coreManagedDialing: true);
                result.Data = AdaptiveRouting.XrayTunPrivacyConfig.Apply(result.Data.ToString(), context.ProtectDomainList,
                    context.SimpleDnsItem.BootstrapDNS ?? "119.29.29.29", context.IsWindows);
                result.Data = AdaptiveRouting.XrayRoutingHotReload.Prepare(result.Data.ToString(), AppManager.Instance.RoutingApiPort);
            }
            catch (Exception ex)
            {
                return new RetResult { Success = false, Msg = $"分流规则配置失败：{ex.Message}" };
            }
        }
        else if (context.AppConfig.LimitedLearning?.Enabled == true)
        {
            return new RetResult { Success = false, Msg = "有限学习仅支持标准 Xray 配置，不支持自定义内核配置或 sing-box。" };
        }
        if (fileName.IsNotEmpty() && result.Data != null)
        {
            await File.WriteAllTextAsync(fileName, result.Data.ToString());
        }

        return result;
    }

    private static async Task<RetResult> GenerateClientCustomConfig(ProfileItem node, string? fileName)
    {
        var ret = new RetResult();
        try
        {
            if (node == null || fileName is null)
            {
                ret.Msg = ResUI.CheckServerSettings;
                return ret;
            }

            if (File.Exists(fileName))
            {
                File.SetAttributes(fileName, FileAttributes.Normal); //If the file has a read-only attribute, direct deletion will fail
                File.Delete(fileName);
            }

            var addressFileName = node.Address;
            if (!File.Exists(addressFileName))
            {
                addressFileName = Utils.GetConfigPath(addressFileName);
            }
            if (!File.Exists(addressFileName))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }
            File.Copy(addressFileName, fileName);
            File.SetAttributes(fileName, FileAttributes.Normal); //Copy will keep the attributes of addressFileName, so we need to add write permissions to fileName just in case of addressFileName is a read-only file.

            //check again
            if (!File.Exists(fileName))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;
            return await Task.FromResult(ret);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    public static async Task<RetResult> GenerateClientSpeedtestConfig(Config config, string fileName, List<ServerTestItem> selecteds, ECoreType coreType, Action<CoreConfigContext>? contextReady = null)
    {
        var result = new RetResult();
        var dummyNode = new ProfileItem
        {
            CoreType = coreType
        };
        var builderResult = await CoreConfigContextBuilder.Build(config, dummyNode, false);
        var context = builderResult.Context;
        foreach (var testItem in selecteds)
        {
            var node = testItem.Profile;
            var (actNode, validation) = await CoreConfigContextBuilder.ResolveNodeAsync(context, node, true);
            if (!validation.Success) return new RetResult(false, string.Join("; ", validation.Errors));
            if (node.IndexId == actNode.IndexId)
            {
                continue;
            }
            context.ServerTestItemMap[node.IndexId] = actNode.IndexId;
        }
        contextReady?.Invoke(context);
        if (coreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(selecteds);
        }
        else if (coreType == ECoreType.Xray)
        {
            result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(selecteds);
        }
        if (result.Success != true)
        {
            return result;
        }
        await File.WriteAllTextAsync(fileName, result.Data.ToString());
        return result;
    }

    public static async Task<RetResult> GenerateClientSpeedtestConfig(Config config, CoreConfigContext context, ServerTestItem testItem, string fileName)
    {
        var result = new RetResult();
        var initPort = AppManager.Instance.GetLocalPort(EInboundProtocol.speedtest);
        var port = Utils.GetFreePort(initPort + testItem.QueueNum);
        testItem.Port = port;

        if (context.RunCoreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(port);
        }
        else
        {
            result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(port);
        }
        if (result.Success != true)
        {
            return result;
        }

        await File.WriteAllTextAsync(fileName, result.Data.ToString());
        return result;
    }
}
