namespace Qoder2Api.Configuration;

/// <summary>
/// 服务运行参数：监听地址与数据库位置。
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string? Urls { get; init; }

    public string DatabasePath { get; init; } = Path.Combine("save", "SaveData.db");
}
