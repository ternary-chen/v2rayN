namespace AdaptiveRouting;

public sealed class AiPolicyOptions
{
    public bool Enabled { get; set; }
    public string AiProfileId { get; set; } = "";
    // The existing Config.IndexId is the default-policy node, so main-list selection stays consistent.
}
