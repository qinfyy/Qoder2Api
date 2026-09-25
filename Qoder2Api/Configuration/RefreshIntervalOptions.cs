namespace Qoder2Api.Configuration;

/// <summary>后台刷新周期。设为 0 表示关闭该项。</summary>
public sealed class RefreshIntervalOptions
{
    public const string SectionName = "Refresh";

    /// <summary>模型目录同步周期。0 = 关闭。</summary>
    public TimeSpan ModelCatalog { get; init; } = TimeSpan.FromMinutes(2);
}
