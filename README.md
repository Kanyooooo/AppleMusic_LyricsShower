# AppleMusic Translator

Windows Apple Music 歌词翻译悬浮窗。它会读取 Apple Music 当前播放信息，优先从 Apple Music 进程内存里抓官方 TTML 同步歌词，再后台懒加载翻译。

这个项目是 `AppleMusic_LyricsShower` 的重写版：核心仍然是内存取证式抓歌词，但现在更偏向稳定、低误判和日常可用。

## 功能

- 识别 Windows Apple Music 当前曲目、歌手、专辑、进度和时长。
- 扫描 Apple Music 进程内存，解析官方 TTML 同步歌词。
- 自动尝试通过 UIAutomation 打开 Apple Music 歌词面板，Apple Music 在后台时也尽量触发懒加载。
- 使用 Apple Music 右侧歌词面板的可见文本做兜底，处理部分歌曲没有连续 TTML XML 的情况。
- 第一次没抓到时会自动延迟重扫一次，减少切歌瞬间歌词还没进内存导致的漏抓。
- 已命中的整首歌词会缓存到本机；下次切歌先秒开缓存歌词，再后台校验 Apple Music 内存。
- 提供“不是这首歌”按钮，可以删除当前歌曲的歌词缓存并强制重扫。
- 如果同一份歌词缓存疑似被不同歌曲共用，按钮会显示冲突提示，方便人工判断。
- 后台懒加载翻译，先显示歌词，翻译缓存命中后自动补上中文。
- 支持居中歌词和 Apple Music 风格竖向歌词列表。
- 支持显示/隐藏翻译、歌词提前量、字体大小、文字颜色、背景色、强调色、背景透明度。
- 支持自动文字对比度，深色/浅色背景下尽量避免文字糊成一团。
- 支持拖动歌词区域微调显示位置，也可以在设置里用滑杆调 X/Y 偏移。
- 支持仅歌词模式，并提供托盘菜单找回设置窗口。
- 歌词窗口会持续保持最高层显示。
- LRCLIB 备用源：内存歌词和可见歌词都不可用时再尝试。

## 匹配策略

为避免串歌，匹配逻辑比较保守：

- 有 Apple Music 可见歌词锚点时，要求内存 TTML 候选命中这些锚点。
- 没有锚点时，只接受时长非常接近且明显优于其他候选的结果。
- 如果官方 TTML 没有命中，但右侧歌词面板能读到文本，会切到 `Apple Music visible lyrics` 兜底模式。
- 兜底模式会定时读取右侧面板当前可见歌词，因此请保持 Apple Music 歌词面板打开。
- 本地歌词缓存只用于快速显示；后台仍会尝试用实时内存结果替换。

## 运行

需要 Windows 10 19041+ / Windows 11，以及 .NET 8 SDK。

```powershell
dotnet run .\AppleMusicTranslator.csproj
```

只做编译检查：

```powershell
dotnet build .\AppleMusicTranslator.csproj -c Release
```

## 打包

发布成单文件 exe：

```powershell
dotnet publish .\AppleMusicTranslator.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:IncludeNativeLibrariesForSelfExtract=true
```

默认输出在：

```text
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\AppleMusicTranslator.exe
```

## 使用建议

1. 先打开 Apple Music 并播放歌曲。
2. 推荐打开 Apple Music 右侧歌词面板；工具也会尝试自动打开它。
3. 启动本工具。
4. 如果提示没有歌词，点刷新按钮或托盘菜单里的“重新扫描歌词”。
5. 如果歌词串歌，点右上角 `!` 按钮删除当前歌曲歌词缓存并重新扫描。

切歌时工具会自动快速扫描；如果第一次没赶上 Apple Music 写入歌词内存，会自动再扫描一次。

设置里可以切换竖向歌词、调整提前量、颜色和位置。拖动歌词区域本身可以快速调整歌词相对窗口的位置。

## 数据与缓存

翻译缓存只保存“单句原文 -> 中文翻译”：

```text
%AppData%\AppleMusicTranslator\translation-cache.json
```

歌词缓存保存“歌曲 -> 整首歌词”，用于下次切歌秒开：

```text
%AppData%\AppleMusicTranslator\lyrics-cache.json
```

本工具会读取 Apple Music 进程内存来寻找歌词，不会修改 Apple Music 进程。

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
- UIAutomation 打开歌词面板不是进程注入式 hook，不会修改 Apple Music；如果 Apple Music 控件名变化，可能需要重新适配。
