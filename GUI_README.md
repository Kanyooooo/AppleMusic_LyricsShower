# Apple Music 桌面歌词显示器 - GUI 版本

## 安装依赖

```bash
pip install PySide6 winsdk pymem psutil
```

## 文件结构

```
AppleMusic_lyricsShower/
├── gui_main.py          # GUI 主程序
├── backend_engine.py    # 后端引擎（QThread）
├── logo.png            # 系统托盘图标（可选）
└── dashang.png         # 打赏码图片（可选）
```

## 运行程序

```bash
python gui_main.py
```

## 功能特性

### 1. 桌面歌词悬浮窗
- ✅ 无边框、窗口置顶、背景透明
- ✅ 鼠标拖拽移动
- ✅ 大字号带阴影效果
- ✅ 锁定模式（鼠标穿透）

### 2. 系统托盘
- ✅ 显示/隐藏歌词
- ✅ 锁定歌词（鼠标穿透）
- ✅ 设置面板
- ✅ 支持/联系作者
- ✅ 安全退出

### 3. 设置面板
- ✅ 字体颜色选择器
- ✅ 透明度滑块
- ✅ 功能预留区

### 4. 支持面板
- ✅ 开发者邮箱显示
- ✅ 打赏码图片展示

### 5. 后端引擎（QThread）
- ✅ 异步媒体信息获取
- ✅ 内存 TTML 扫描
- ✅ 智能切歌检测
- ✅ 毫秒级歌词同步
- ✅ 信号槽通信（不阻塞 UI）

## 架构说明

### 信号槽系统
```python
BackendEngine (QThread)
├── lyric_updated(str)      → 更新歌词文本
├── song_changed(str, str)  → 切歌通知（标题、歌手）
└── status_changed(str)     → 状态改变（playing/paused/stopped）
```

### 鼠标穿透实现
使用 Windows API 设置窗口扩展样式：
- `WS_EX_TRANSPARENT` - 鼠标穿透
- `WS_EX_LAYERED` - 分层窗口

## 使用说明

1. **启动程序** - 运行 `python gui_main.py`
2. **播放音乐** - 在 Apple Music 中播放歌曲
3. **自动同步** - 歌词会自动显示并同步
4. **拖动位置** - 鼠标左键拖动歌词窗口
5. **锁定歌词** - 右键托盘图标 → 锁定歌词（启用鼠标穿透）
6. **自定义样式** - 右键托盘图标 → 设置 → 调整颜色和透明度

## 注意事项

1. **管理员权限** - 首次运行可能需要管理员权限（内存读取）
2. **winsdk 依赖** - 必须安装 `winsdk` 才能获取系统媒体信息
3. **图标文件** - `logo.png` 和 `dashang.png` 为可选文件

## 故障排除

### 问题：无法获取媒体信息
- 确保已安装 `winsdk`: `pip install winsdk`
- 确保 Apple Music 正在运行

### 问题：无法读取内存
- 以管理员身份运行程序
- 参考之前的 UWP 沙箱绕过方案

### 问题：歌词不同步
- 检查后端引擎是否正常运行
- 查看控制台输出的错误信息

## 开发者信息

- 邮箱：1531516107@qq.com
- 项目：Apple Music 桌面歌词显示器
- 技术栈：PySide6 + winsdk + pymem
