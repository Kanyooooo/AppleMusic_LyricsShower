# AppleMusic Translator

Windows Apple Music 歌词翻译悬浮窗。它会读取 Apple Music 当前播放信息，优先从 Apple Music 进程内存中抓取官方 TTML 同步歌词，先显示歌词，再后台懒加载翻译。

这个项目是 `AppleMusic_LyricsShower` 的重写版：核心仍然是内存取证式抓歌词，但现在更偏向稳定、低误判和日常可用。

## 功能

- 识别 Windows Apple Music 当前曲目、歌手、专辑、进度和时长。
- 扫描 Apple Music 进程内存，解析官方 TTML 同步歌词。
- 自动尝试通过 UIAutomation 打开 Apple Music 歌词面板，Apple Music 在后台时也尽量触发歌词加载。
- 读取 Apple Music 右侧歌词面板的可见文本作为锚点，降低串歌概率。
- 第一次没抓到时会自动延迟重扫一次，减少切歌瞬间歌词还没进入内存导致的漏抓。
- 已命中的整首歌词会缓存到本机 SQLite 数据库，下次切歌先秒开缓存，再后台校验实时内存。
- 提供“不是这首歌”按钮，可以删除当前曲目的歌词缓存并强制重扫。
- 如果同一份歌词缓存疑似被不同歌曲共用，按钮会显示冲突提示，方便人工判断。
- 后台懒加载翻译，先显示原文，翻译缓存命中后自动补上中文。
- 支持居中歌词、竖向歌词列表、顶部灵动岛模式。
- 竖向歌词支持长屏幕自动滚动，并可将当前歌词自动居中。
- 支持显示/隐藏翻译、歌词提前量、字号、文字颜色、背景色、强调色、背景透明度。
- 支持自动文字对比度，深色/浅色背景下尽量避免文字和背景糊在一起。
- 支持拖动歌词区域微调显示位置，也可以在设置中用滑杆调 X/Y 偏移。
- 支持仅歌词模式，并提供托盘菜单找回设置窗口。
- 窗口、样式和自定义参数会记忆到本机设置文件。
- 歌词窗口会持续保持最高层显示。
- LRCLIB 备用源：内存歌词和可见歌词都不可用时再尝试。

## 显示模式

- 居中歌词：默认模式，适合普通桌面悬浮。
- 竖向歌词：类似 Apple Music 的纵向歌词列表，当前歌词可自动居中并平滑滚动。
- 灵动岛模式：参考 Lyricify 的顶部歌词岛体验，窗口会吸附在屏幕上侧，显示更紧凑的原文/翻译。

灵动岛宽度、高度和顶部间距都可以在设置中调整。进入仅歌词或灵动岛后，仍然可以从系统托盘右键打开设置。

## 匹配策略

为了避免串歌，匹配逻辑比较保守：

- 有 Apple Music 可见歌词锚点时，要求内存 TTML 候选命中这些锚点。
- 没有锚点时，只接受时长非常接近且明显优于其他候选的结果。
- 如果官方 TTML 没有命中，但右侧歌词面板能读到文本，会切到 `Apple Music visible lyrics` 兜底模式。
- 兜底模式会定时读取右侧面板当前可见歌词，因此请尽量保持 Apple Music 歌词面板打开。
- 本地歌词缓存只用于快速显示；后台仍会尝试用实时内存结果替换缓存。

## 数据与缓存

翻译缓存只保存“单句原文 -> 中文翻译”：

```text
%AppData%\AppleMusicTranslator\translation-cache.json
```

歌词缓存保存“歌曲 -> 整首同步歌词”，用于下次切歌秒开：

```text
%AppData%\AppleMusicTranslator\lyrics-cache.db
```

旧版本的 `lyrics-cache.json` 会在第一次启动时自动迁移到 SQLite 数据库。数据库会自动创建索引，并按歌曲 key / 歌词 fingerprint 查询，避免每次启动解析整包 JSON。

用户自定义设置会保存在：

```text
%AppData%\AppleMusicTranslator\settings.json
```

本工具会读取 Apple Music 进程内存来寻找歌词，不会修改 Apple Music 进程，也不会注入 Apple Music。

## 运行

普通用户请下载 Release 里的 `AppleMusicTranslator.exe` 单文件版，双击即可运行；不需要安装 .NET。

如果 Windows SmartScreen 提示“Windows 已保护你的电脑”，这是因为当前构建未做代码签名。确认来源可信后，点“更多信息” -> “仍要运行”即可。

从源码运行需要 Windows 10 19041+ / Windows 11，以及 .NET 8 SDK。

```powershell
dotnet run .\AppleMusicTranslator.csproj
```

只做编译检查：

```powershell
dotnet build .\AppleMusicTranslator.csproj -c Release
dotnet build .\Diagnostics\AppleMusicTranslator.Diagnostics.csproj -c Release
```

## 打包

发布普通用户可直接下载的自包含便携版：

```powershell
.\scripts\Publish-Portable.ps1
```

脚本会生成自包含单文件 exe，并同时打包 zip。没有传 `-Tag` 时会输出到临时 dev 目录：

```text
Release\dev-yyyyMMdd-HHmmss\portable-win-x64\AppleMusicTranslator.exe
Release\dev-yyyyMMdd-HHmmss\AppleMusicTranslator-dev-yyyyMMdd-HHmmss-win-x64-portable.zip
```

正式 GitHub Release 建议先提交源码并打 tag，然后执行：

```powershell
.\scripts\Publish-Portable.ps1 -Tag v0.2.0-beta.1
```

GitHub Release 优先上传 `AppleMusicTranslator-<tag>-win-x64-portable.zip` 和 `SHA256SUMS.txt`。zip 里面带有 `README.txt`，用户解压后双击 `AppleMusicTranslator.exe` 即可运行。

如果需要手动执行发布命令：

```powershell
dotnet publish .\AppleMusicTranslator.csproj -c Release -r win-x64 --self-contained true -o .\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish-portable-single /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false
```

手动命令的输出文件：

```text
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish-portable-single\AppleMusicTranslator.exe
```

不要只上传 framework-dependent 目录里的小 exe；那种版本需要同时携带同目录 dll，且用户电脑需要 .NET Desktop Runtime。普通用户如果只下载那个小 exe，就会出现打不开、提示安装 .NET 或误以为安装失败的问题。

## 使用建议

1. 先打开 Apple Music 并播放歌曲。
2. 推荐打开 Apple Music 右侧歌词面板；工具也会尝试自动打开它。
3. 启动本工具。
4. 如果提示没有歌词，点刷新按钮或托盘菜单里的“重新扫描歌词”。
5. 如果歌词串歌，点右上角 `!` 按钮删除当前歌曲歌词缓存并重新扫描。

切歌时工具会自动快速扫描；如果第一次没赶上 Apple Music 写入歌词内存，会自动再扫描一次。

## 诊断

仓库里带了一个诊断项目，可以查看当前曲目、可见歌词锚点和内存 TTML 候选：

```powershell
dotnet run --project .\Diagnostics\AppleMusicTranslator.Diagnostics.csproj -c Release
```

搜索进程内存中的某段文本：

```powershell
dotnet run --project .\Diagnostics\AppleMusicTranslator.Diagnostics.csproj -c Release -- search "歌词片段"
```

## 已知限制

- Apple Music 的 Windows 客户端不同版本内存布局差异很大，部分歌曲可能只有渲染后的歌词文本，没有完整连续 TTML XML。
- 可见歌词兜底依赖 Apple Music 右侧歌词面板；自动打开失败时，需要手动打开一次。
- 可见歌词兜底没有官方逐行时间戳，只适合作为“能看到和翻译当前歌词”的保底方案。
- UIAutomation 打开歌词面板不是进程注入式 hook，不会修改 Apple Music；如果 Apple Music 控件结构变化，可能需要重新适配。

## 参考

- [Lyricify App](https://github.com/WXRIW/Lyricify-App)：顶部歌词岛、桌面歌词等体验方向参考。
- [163MusicLyrics](https://github.com/jitwxs/163MusicLyrics)：歌词源和歌词缓存思路参考。
