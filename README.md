```

当前发布：`v1.2.1`。本版本使用 SDK `0.9.8`，需要宿主 `0.9.8` 的新 ABI；旧宿主不兼容。
获取群文件直链 #getlink <FileName>
下载直链到群文件 #savefile <FileLink>
设置最大下载大小 #savemax <SizeMb>
```

插件配置会在命令执行时自动重新加载，修改配置后无需重启插件。

运行要求：宿主最低版本 0.9.8，ShiroBot API 0.9.2；使用 ShiroBot.SDK 0.9.8 和 QQ Model 0.9.8。群文件能力通过 IQFileApi 探测，群号和文件 ID 按原始字符串传递。

配置通过宿主统一接口应用；保存后等待应用完成。修改 config.toml 也由宿主监听并应用，无需重新加载插件。
