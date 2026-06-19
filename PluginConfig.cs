using ShiroBot.SDK.Config;

namespace ShiroBot.Plugin.FileLink;

public sealed class PluginConfig
{
    [ConfigField("只允许管理员#save下载文件")]
    public bool OnlyAllowAdminSaveCommand { get; set; } = true;

    [ConfigField("下载文件使用的 HTTP 代理，留空则不使用，例如 http://127.0.0.1:7890")]
    public string HttpProxy { get; set; } = string.Empty;

    [ConfigField("最大下载文件大小（MB），小于等于 0 表示不限制")]
    public int MaxDownloadFileSizeMb { get; set; } = 4000;
}
