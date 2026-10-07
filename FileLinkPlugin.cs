using LightDl;
using System.Net;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;

using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "0.9.8")]

[assembly: ShiroBotApiCompatibility("0.9.2", "0.9.2")]

namespace ShiroBot.Plugin.FileLink;

[BotPlugin(id:"FileLinkPlugin",
    Description = "用于群聊获取文件直链 / 保存直链到群内",
    Author = "greepar",
    Category = PluginCategory.Utility,
    Version = "1.2.0",
    GithubRepo = "ShirokaProject/Shirobot.Plugin.FileLink",
    IsPluginSingleFile = true,
    SharedAssemblies = "ShiroBot.Model.QQ;ShiroBot.Model.Discord;ShiroBot.Model.Telegram")]
public sealed class FileLinkPlugin : PluginBase
{
    private readonly object _replySubscriptionsLock = new();
    private readonly HashSet<IReplySubscription> _replySubscriptions = [];
    private PluginConfig _config = new();
    private IDisposable? _configWatcher;

    protected override void ConfigureRoutes()
    {
        GroupCommands.MapPrefix("#savefile", HandleSaveFileAsync);
        GroupCommands.MapPrefix("#savemax", HandleSaveMaxAsync);
        GroupCommands.MapPrefix("#getlink", HandleGetLinkAsync);
    }

    protected override Task LoadAsync()
    {
        _config = Context.Config.Load<PluginConfig>();
        _configWatcher = Context.Config.Watch<PluginConfig>(config => _config = config);
        return Task.CompletedTask;
    }

    protected override Task OnUnloadAsync()
    {
        _configWatcher?.Dispose();

        lock (_replySubscriptionsLock)
        {
            foreach (var subscription in _replySubscriptions)
            {
                subscription.Dispose();
            }

            _replySubscriptions.Clear();
        }

        return Task.CompletedTask;
    }

    private async Task HandleSaveFileAsync(MessageEvent message)
    {
        try
        {
            var config = _config;
            if (config.OnlyAllowAdminSaveCommand && !Context.IsAdmin(message.Reference.InstanceId is { } instanceId ? new UserReference(instanceId, message.Sender.Id) : throw new InvalidOperationException("消息缺少来源实例。")))
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
            var groupId = GetQqGroupId(message);
            var fileApi = GetFileApi();

            var tempPath = Path.Combine(Context.PluginDirectory, ".tmp");
            Directory.CreateDirectory(tempPath);

            using var cts = new CancellationTokenSource();
            var maxBytes = config.MaxDownloadFileSizeMb > 0
                ? config.MaxDownloadFileSizeMb * 1024L * 1024L
                : long.MaxValue;
            LightDownloadFileInfo? remoteFileInfo = null;
            long rejectedSize = -1;

            var request = LightDownloadRequest.ToDirectory(url.ToString(), tempPath)
                .OnFileInfo(info =>
                {
                    remoteFileInfo = info;
                    if (info.Size > maxBytes)
                    {
                        Interlocked.Exchange(ref rejectedSize, info.Size);
                        cts.Cancel();
                    }
                })
                .OnProgress(p =>
                {
                    if (p.DownloadedBytes > maxBytes)
                    {
                        Interlocked.Exchange(ref rejectedSize, p.DownloadedBytes);
                        BotLog.Info($"文件过大，大小: {p.DownloadedBytes / 1024d / 1024d:F2} MB，超过最大限制 {config.MaxDownloadFileSizeMb} MB,取消下载");
                        cts.Cancel();
                    }
                    BotLog.Info($"\r{p.ProgressPercentage:F1}%  {p.Speed / 1024d / 1024d:F1} MB/s");
                });
            BotLog.Info($"开始下载文件: {url}");
            LightDownloadResult dlResult;
            try
            {
                var downloadConfig = new LightDownloadConfig
                {
                    Proxy = CreateProxy(config.HttpProxy)
                };
                dlResult = await LightDownload.DownloadAsync(request, downloadConfig, cts.Token);
            }
            catch (OperationCanceledException) when (Interlocked.Read(ref rejectedSize) >= 0)
            {
                await Context.Message.ReplyAsync(message,
                    $"文件过大，大小: {Interlocked.Read(ref rejectedSize) / 1024d / 1024d:F2} MB，超过最大限制 {config.MaxDownloadFileSizeMb} MB");
                return;
            }

            var filePath = dlResult.FilePath;
            var fileName = dlResult.FileName;

            if (remoteFileInfo is not null)
            {
                await Context.Message.ReplyAsync(message,
                    $"获取到文件信息: {remoteFileInfo.FileName}，大小: {remoteFileInfo.Size / 1024d / 1024d:F2} MB");
            }

            BotLog.Info($"下载完成,临时保存到目录: {dlResult.FilePath}");
            await Context.Message.ReplyAsync(message, "下载完成,开始上传.");

            try
            {
                var fileUri = new Uri(filePath).AbsoluteUri;
                BotLog.Info($"开始上传群文件: {fileName} -> {groupId}");
                await fileApi.UploadGroupFileAsync(groupId, fileUri, fileName);
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

    private async Task HandleSaveMaxAsync(MessageEvent message)
    {
        if (!Context.IsAdmin(message.Reference.InstanceId is { } instanceId ? new UserReference(instanceId, message.Sender.Id) : throw new InvalidOperationException("消息缺少来源实例。")))
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

    private async Task HandleGetLinkAsync(MessageEvent message)
    {
        var text = message.GetPlainText().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (text.Length < 2)
        {
            await Context.Message.ReplyAsync(message, "请输入正确的命令格式: #getlink <文件名> [序号]");
            return;
        }

        var name = text[1];
        var groupId = GetQqGroupId(message);
        var fileApi = GetFileApi();
        var files = new List<QGroupFile>();

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
                    var url = await fileApi.GetGroupFileDownloadUrlAsync(groupId, file.FileId);
                    await Context.Message.ReplyAsync(message, $"找到文件:\n{file.FileName}\n下载链接: {url}");
                    return;
                }
                default:
                {
                    if (result.Count > 60)
                    {
                        await Context.Message.ReplyAsync(message, $"结果过多,请输入更精确的关键字。");
                        return;
                    }
                    var fileListString = string.Join("\n",
                        result.Select((f, i) =>
                            $"{i + 1}. {f.FileName}  ({f.UploadedTime?.ToLocalTime().DateTime})"));
                    var subMsg = await Context.Message.ReplyAsync(message,
                        $"找到多个文件 ({result.Count}):\n{fileListString}\n\n在10分钟内用序号回复此消息获取下载链接.");

                    IReplySubscription? subscription = null;
                    subscription = Context.Message.SubscribeReply(subMsg.Reference ?? new MessageReference(message.Reference.InstanceId, message.Channel, subMsg.MessageId), TimeSpan.FromMinutes(10), async replyMessage =>
                    {
                        if (replyMessage.Channel.Id == message.Channel.Id &&
                            int.TryParse(replyMessage.GetPlainText(), out var replyIndex) &&
                            replyIndex > 0 && replyIndex <= result.Count)
                        {
                            replyIndex -= 1;
                            var file = result[replyIndex];
                            try
                            {
                                var url = await fileApi.GetGroupFileDownloadUrlAsync(groupId, file.FileId);
                                await Context.Message.ReplyAsync(replyMessage, $"文件:\n{file.FileName}\n下载链接: {url}");
                            }
                            finally
                            {
                                DisposeReplySubscription(subscription);
                            }
                        }
                        else
                        {
                            await Context.Message.ReplyAsync(replyMessage, "请输入正确的序号。");
                        }
                    }, false);
                    lock (_replySubscriptionsLock)
                    {
                        _replySubscriptions.Add(subscription);
                    }
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
            var response = await fileApi.GetGroupFilesAsync(groupId, parentFolderId);
            files.AddRange(response.Files);

            foreach (var folder in response.Folders)
            {
                await CollectFilesAsync(folder.FolderId);
            }
        }
    }

    private IQFileApi GetFileApi() =>
        Context.GetAdapterExtension<IQFileApi>()
        ?? throw new NotSupportedException("当前 QQ 适配器不支持群文件操作。");

    private static string GetQqGroupId(MessageEvent message) =>
        message.Channel.Type == ChannelType.Group && string.Equals(message.Platform, "qq", StringComparison.OrdinalIgnoreCase)
            ? message.Channel.Id
            : throw new NotSupportedException("当前消息不是有效的 QQ 群消息。");

    private static WebProxy? CreateProxy(string? httpProxy)
    {
        if (string.IsNullOrWhiteSpace(httpProxy))
        {
            return null;
        }

        return Uri.TryCreate(httpProxy.Trim(), UriKind.Absolute, out var proxyUri)
            ? new WebProxy(proxyUri)
            : throw new ArgumentException($"HTTP 代理地址格式错误: {httpProxy}");
    }

    private void DisposeReplySubscription(IReplySubscription? subscription)
    {
        if (subscription is null)
        {
            return;
        }

        subscription.Dispose();
        lock (_replySubscriptionsLock)
        {
            _replySubscriptions.Remove(subscription);
        }
    }
}
