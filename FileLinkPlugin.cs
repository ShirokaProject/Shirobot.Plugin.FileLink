using ShiroBot.Model.Common;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.FileLink;

[BotPlugin(id:"FileLinkPlugin",
    Description = "用于群聊获取文件直链 / 保存直链到群内",
    Author = "greepar",
    Category = PluginCategory.Utility,
    Version = "1.1.0",
    GithubRepo = "ShirokaProject/Shirobot.Plugin.FileLink",
    IsPluginSingleFile = false)]
public sealed class FileLinkPlugin : PluginBase
{
    private PluginConfig _config = new();
    protected override Task LoadAsync()
    {
        _config = Context.Config.Load<PluginConfig>();
        GroupCommands.MapPrefix("#savefile", HandleSaveFileAsync);
        GroupCommands.MapPrefix("#savemax", HandleSaveMaxAsync);
        GroupCommands.MapPrefix("#getlink", HandleGetLinkAsync);
        return Task.CompletedTask;
    }

    protected override Task OnUnloadAsync()
    {
        return Task.CompletedTask;
    }

    private async Task HandleSaveFileAsync(GroupIncomingMessage message)
    {
        try
        {
            var config = ReloadConfig();

            if ( config.OnlyAllowAdminSaveCommand &&
                !(Context.OwnerList.Contains(message.SenderId) || Context.AdminList.Contains(message.SenderId)) )
            {
                await Context.Message.ReplyAsync(message, "只有管理员和主人可以使用此命令。");
                return;
            }
            var text = message.GetPlainText().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (text.Length != 2 || !text[1].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                await Context.Message.ReplyAsync(message, "请输入正确的命令格式: #savefile <文件URL>");
                return;
            }

            var url = new Uri(text[1]);
            BotLog.Info($"正在处理保存文件命令，URL: {url}");

            var metadata = await DownloadHelper.GetDownloadMetadataAsync(url, config.HttpProxy, CancellationToken.None);
            var maxMb = config.MaxDownloadFileSizeMb;
            var maxSize = maxMb * 1024L * 1024L;

            if (maxMb > 0 && metadata.TotalBytes is not null)
            {
                BotLog.Info($"HEAD 获取到文件大小: {metadata.TotalBytes}");

                if (metadata.TotalBytes > maxSize)
                {
                    await Context.Message.ReplyAsync(message,
                        $"文件过大: {metadata.TotalBytes / 1024 / 1024} MB, 超过 {maxMb} MB");
                    return;
                }
            }

            var fileName = metadata.SuggestedFileName;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = Path.GetFileName(url.LocalPath);
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"file_{Guid.NewGuid():N}";
            }

            fileName = Uri.UnescapeDataString(fileName);
            fileName = Path.GetFileName(fileName);

            var tempPath = Path.Combine(Path.GetTempPath(), "ShiroBot", "downloads");
            Directory.CreateDirectory(tempPath);
            var filePath = Path.Combine(tempPath, $"{Guid.NewGuid():N}_{fileName}");

            await Context.Message.ReplyAsync(message,
                $"开始下载: {fileName}\n大小: {(metadata.TotalBytes != null ? metadata.TotalBytes / 1024 / 1024 + " MB" : "未知")}");

            await DownloadHelper.DownloadWithProgressAsync(
                url,
                filePath,
                metadata,
                fileName,
                config.HttpProxy,
                CancellationToken.None);

            var downloadedFileInfo = new FileInfo(filePath);
            if (!downloadedFileInfo.Exists)
            {
                throw new FileNotFoundException("下载完成后未找到目标文件。", filePath);
            }

            if (maxMb > 0 && downloadedFileInfo.Length > maxSize)
            {
                File.Delete(filePath);
                await Context.Message.ReplyAsync(message, "文件超过限制（下载中断）");
                return;
            }

            BotLog.Info($"下载完成: {filePath}");
            await Context.Message.ReplyAsync(message, "下载完成.");

            try
            {
                var fileUri = new Uri(filePath).AbsoluteUri;
                BotLog.Info($"开始上传群文件: {fileName} -> {message.Group.GroupId}");
                await Context.File.UploadGroupFileAsync(message.Group.GroupId, fileUri, fileName, "/");
            }
            finally
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch (Exception e)
        {
            BotLog.Error($"保存文件失败: {e}");
            await Context.Message.ReplyAsync(message, $"发生错误: {e.Message}");
        }
    }

    private async Task HandleSaveMaxAsync(GroupIncomingMessage message)
    {
        ReloadConfig();

        if (!(Context.OwnerList.Contains(message.SenderId) || Context.AdminList.Contains(message.SenderId)))
        {
            await Context.Message.ReplyAsync(message, "只有管理员和主人可以使用此命令。");
            return;
        }

        var text = message.GetPlainText().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (text.Length != 2 || !int.TryParse(text[1], out var maxMb) || maxMb < 0)
        {
            await Context.Message.ReplyAsync(message, "请输入正确的命令格式: #savemax <最大MB，0表示不限制>");
            return;
        }

        Context.Config.SetValue(nameof(PluginConfig.MaxDownloadFileSizeMb), maxMb);
        _config.MaxDownloadFileSizeMb = maxMb;

        await Context.Message.ReplyAsync(message, maxMb == 0
            ? "已取消保存文件大小限制。"
            : $"已设置最大保存文件大小为 {maxMb} MB。");
    }

    private async Task HandleGetLinkAsync(GroupIncomingMessage message)
    {
        ReloadConfig();

        var text = message.GetPlainText().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (text.Length < 2)
        {
            await Context.Message.ReplyAsync(message, "请输入正确的命令格式: #getlink <文件名> [序号]");
            return;
        }

        var name = text[1];
        var groupId = message.Group.GroupId;
        var files = new List<GroupFileEntity>();

        BotLog.Info("正在处理获取链接命令...");

        await CollectFilesAsync("/");

        var result = files
            .Where(f => f.FileName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.FileName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.FileName.Length)
            .ToList();

        try
        {
            switch (result.Count)
            {
                case 0:
                    await Context.Message.ReplyAsync(message, $"未找到包含 '{name}' 的文件。");
                    return;
                case 1:
                {
                    var file = result[0];
                    var url = await Context.File.GetGroupFileDownloadUrlAsync(groupId, file.FileId);
                    await Context.Message.ReplyAsync(message, $"找到文件:\n{file.FileName}\n下载链接{url.DownloadUrl}{file.FileName}");
                    return;
                }
                default:
                {
                    if (text.Length == 3 && int.TryParse(text[2], out var index) &&
                        (index > 0 && index <= result.Count ? true : throw new Exception("请输入正确序号")))
                    {
                        index -= 1;
                        var file = result[index];
                        var targetFileId = file.FileId;
                        var url = await Context.File.GetGroupFileDownloadUrlAsync(groupId, targetFileId);
                        await Context.Message.ReplyAsync(message,
                            $"文件:\n{file.FileName}\n下载链接{url.DownloadUrl}{file.FileName}");
                        return;
                    }

                    var fileListString = string.Join("\n",
                        result.Select((f, i) =>
                            $"{i + 1}. {f.FileName}  ({DateTimeOffset.FromUnixTimeSeconds(f.UploadedTime).ToLocalTime().DateTime})"));

                    await Context.Message.ReplyAsync(message,
                        $"找到多个文件 ({result.Count}):\n{fileListString}\n\n使用方式:\n#getlink {name} <序号>");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            BotLog.Error($"发生错误: {ex.Message}");
            await Context.Message.ReplyAsync(message, ex.Message);
        }

        return;

        async Task CollectFilesAsync(string parentFolderId)
        {
            var response = await Context.File.GetGroupFilesAsync(groupId, parentFolderId);
            files.AddRange(response.Files);

            foreach (var folder in response.Folders)
            {
                await CollectFilesAsync(folder.FolderId);
            }
        }
    }

    private PluginConfig ReloadConfig()
    {
        _config = Context.Config.Load<PluginConfig>();
        return _config;
    }

}
