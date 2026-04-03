"""
Apple Music 桌面歌词显示器 - GUI 版本
使用 PySide6 构建现代化图形界面
"""

import sys
import os
import json  # 引入 json 模块
from pathlib import Path
from PySide6.QtWidgets import (
    QApplication, QMainWindow, QLabel, QSystemTrayIcon, QMenu,
    QDialog, QVBoxLayout, QHBoxLayout, QTabWidget, QWidget,
    QPushButton, QSlider, QColorDialog, QFileDialog, QCheckBox,
    QGraphicsOpacityEffect
)
from PySide6.QtCore import (
    Qt, QThread, Signal, QPoint, QTimer, QSize, QPropertyAnimation,
    QEasingCurve
)
from PySide6.QtGui import (
    QFont, QColor, QPalette, QIcon, QPixmap, QPainter,
    QCursor, QAction, QBrush
)
import ctypes
from ctypes import wintypes

# 导入后端引擎
from backend_engine import BackendEngine, LyricLine

class SettingsManager:
    """带 JSON 持久化的配置管理器"""
    def __init__(self):
        # 配置文件存放在脚本同目录
        self.config_file = Path(__file__).parent / "config.json"
        
        # 默认配置（时间校准默认改为 +1.0 秒）
        self.settings = {
            'text_color': '#FFFFFF',
            'text_opacity': 1.0,
            'border_opacity': 0.0,
            'window_width': 1080,
            'lyric_offset': 1.0, 
            'fade_enabled': True
        }
        self.load()

    def load(self):
        """启动时读取本地配置"""
        if self.config_file.exists():
            try:
                with open(self.config_file, 'r', encoding='utf-8') as f:
                    saved_settings = json.load(f)
                    self.settings.update(saved_settings)
            except Exception as e:
                print(f"读取配置失败: {e}")

    def save(self, key, value):
        """当用户在界面修改设置时，立即保存到文件"""
        self.settings[key] = value
        try:
            with open(self.config_file, 'w', encoding='utf-8') as f:
                json.dump(self.settings, f, ensure_ascii=False, indent=4)
        except Exception as e:
            print(f"保存配置失败: {e}")


class DesktopLyricWindow(QMainWindow):
    """桌面歌词悬浮窗"""

    def __init__(self, settings_manager):
        super().__init__()
        self.settings_manager = settings_manager
        
        self.is_locked = False
        self.dragging = False
        self.drag_position = QPoint()
        
        # 初始样式设置，从配置文件读取
        self.text_color = QColor(settings_manager.settings['text_color'])
        self.text_opacity = settings_manager.settings['text_opacity']
        self.border_opacity = settings_manager.settings['border_opacity']
        self.fade_enabled = settings_manager.settings['fade_enabled']
        
        self.setStyleSheet("background: transparent; border: none; margin: 0px; padding: 0px;")
        self.setAttribute(Qt.WidgetAttribute.WA_NoSystemBackground)
        self.setContentsMargins(0, 0, 0, 0) # 封死所有内边距

        # 歌词标签：彻底放弃 CSS 背景和边框
        self.lyric_label = QLabel(self)
        self.lyric_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lyric_label.setWordWrap(True)
        # 让标签无视鼠标事件，直接穿透到窗口背景触发移动
        self.lyric_label.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        # 保持 padding 供内部撑开
        self.lyric_label.setStyleSheet("QLabel { background: transparent; border: 0px; padding: 20px; outline: none; }")

        # 淡入淡出效果
        self.text_opacity_effect = QGraphicsOpacityEffect(self.lyric_label)
        self.text_opacity_effect.setOpacity(self.text_opacity)
        self.lyric_label.setGraphicsEffect(self.text_opacity_effect)
        
        # 动画设置
        self.fade_animation = QPropertyAnimation(self.text_opacity_effect, b"opacity", self)
        self.fade_animation.setEasingCurve(QEasingCurve.Type.InOutQuad)

        self.setCentralWidget(self.lyric_label)

        # 核心设置：完全透明和置顶
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint |
            Qt.WindowType.WindowStaysOnTopHint |
            Qt.WindowType.Tool |
            Qt.WindowType.NoDropShadowWindowHint
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)

        self.resize(settings_manager.settings['window_width'], 120)
        self.center_on_screen()

        # 固定动画信号绑定
        self.next_lyric_text = ""
        self.fade_animation.finished.connect(self._on_fade_out_finished)

        # 刷新字体样式
        self.update_font_style()
    
    def update_font_style(self, color: QColor = None):
        """更新字体样式"""
        if color is not None:
            self.text_color = color

        # 使用抗锯齿极强的 YaHei Bold
        font = QFont("Microsoft YaHei", 32, QFont.Weight.Bold)
        font.setStyleStrategy(QFont.StyleStrategy.PreferAntialias)
        self.lyric_label.setFont(font)
        
        self.lyric_label.setStyleSheet(f"""
            QLabel {{
                color: rgb({self.text_color.red()}, {self.text_color.green()}, {self.text_color.blue()});
                background: transparent;
                border: 0px;
                padding: 20px;
                outline: none;
            }}
        """)
        # 立即更新像素层
        self.update()

    def _on_fade_out_finished(self):
        """淡出完成后的回调"""
        if self.fade_animation.endValue() == 0.0:
            self.lyric_label.setText(self.next_lyric_text)
            
            if self.next_lyric_text != "":
                duration = 250 if len(self.next_lyric_text) > 15 else 350
                self.fade_animation.setDuration(duration)
                self.fade_animation.setStartValue(0.0)
                self.fade_animation.setEndValue(self.text_opacity)
                self.fade_animation.start()

    def set_window_opacity_value(self, opacity: float):
        """设置窗口整体透明度"""
        self.window_opacity = opacity
        self.setWindowOpacity(opacity)

    def set_border_opacity(self, opacity: float):
        """设置背景框透明度"""
        self.border_opacity = opacity
        self.update_font_style()

    def set_text_opacity(self, opacity: float):
        """设置文字透明度"""
        self.text_opacity = opacity
        self.text_opacity_effect.setOpacity(opacity)

    def set_fade_enabled(self, enabled: bool):
        """设置是否启用渐入渐出"""
        self.fade_enabled = enabled

    def set_window_width(self, width: int):
        """设置窗口宽度并重新居中"""
        self.resize(width, self.height())
        self.center_on_screen()

    def center_on_screen(self):
        """居中显示在屏幕底部"""
        screen = QApplication.primaryScreen().geometry()
        x = (screen.width() - self.width()) // 2
        y = screen.height() - self.height() - 100
        self.move(x, y)

    def update_lyric(self, text: str):
        """更新歌词文本，支持动态时长呼吸动效"""
        if text == "":
            if self.fade_enabled:
                self.next_lyric_text = ""
                self.fade_animation.stop()
                self.fade_animation.setDuration(250) 
                self.fade_animation.setStartValue(self.text_opacity_effect.opacity())
                self.fade_animation.setEndValue(0.0)
                self.fade_animation.start()
            else:
                self.lyric_label.setText("")
        else:
            if self.fade_enabled:
                self.next_lyric_text = text
                
                is_long_text = len(text) > 15
                duration = 150 if is_long_text else 250
                
                self.fade_animation.stop()
                self.fade_animation.setDuration(duration) 
                self.fade_animation.setStartValue(self.text_opacity_effect.opacity())
                self.fade_animation.setEndValue(0.0)
                self.fade_animation.start()
            else:
                self.lyric_label.setText(text)

    def set_locked(self, locked: bool):
        """设置锁定状态（鼠标穿透）"""
        self.is_locked = locked

        if locked:
            hwnd = int(self.winId())
            GWL_EXSTYLE = -20
            WS_EX_TRANSPARENT = 0x00000020
            WS_EX_LAYERED = 0x00080000

            user32 = ctypes.windll.user32
            style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
            user32.SetWindowLongW(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED)
        else:
            hwnd = int(self.winId())
            GWL_EXSTYLE = -20
            WS_EX_TRANSPARENT = 0x00000020

            user32 = ctypes.windll.user32
            style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
            user32.SetWindowLongW(hwnd, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT)

    def mousePressEvent(self, event):
        """鼠标按下事件"""
        if not self.is_locked and event.button() == Qt.MouseButton.LeftButton:
            self.dragging = True
            self.drag_position = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
            event.accept()

    def mouseMoveEvent(self, event):
        """鼠标移动事件"""
        if not self.is_locked and self.dragging:
            self.move(event.globalPosition().toPoint() - self.drag_position)
            event.accept()

    def mouseReleaseEvent(self, event):
        """鼠标释放事件"""
        if event.button() == Qt.MouseButton.LeftButton:
            self.dragging = False
            event.accept()


class SettingsDialog(QDialog):
    """设置对话框"""

    color_changed = Signal(QColor)
    border_opacity_changed = Signal(float)
    text_opacity_changed = Signal(float)
    lyric_offset_changed = Signal(float)
    fade_enabled_changed = Signal(bool)
    window_width_changed = Signal(int)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("设置")
        self.setMinimumSize(500, 500)

        tabs = QTabWidget()

        ui_tab = QWidget()
        ui_layout = QVBoxLayout()

        color_layout = QHBoxLayout()
        color_label = QLabel("字体颜色:")
        self.color_button = QPushButton("选择颜色")
        self.color_button.clicked.connect(self.choose_color)
        color_layout.addWidget(color_label)
        color_layout.addWidget(self.color_button)
        color_layout.addStretch()

        border_opacity_layout = QVBoxLayout()
        border_opacity_label = QLabel("背景框透明度:")
        self.border_opacity_slider = QSlider(Qt.Orientation.Horizontal)
        self.border_opacity_slider.setMinimum(0)
        self.border_opacity_slider.setMaximum(100)
        self.border_opacity_slider.valueChanged.connect(self.on_border_opacity_changed)
        self.border_opacity_value_label = QLabel("0%")
        border_opacity_layout.addWidget(border_opacity_label)
        border_opacity_layout.addWidget(self.border_opacity_slider)
        border_opacity_layout.addWidget(self.border_opacity_value_label)

        text_opacity_layout = QVBoxLayout()
        text_opacity_label = QLabel("文字透明度:")
        self.text_opacity_slider = QSlider(Qt.Orientation.Horizontal)
        self.text_opacity_slider.setMinimum(10)
        self.text_opacity_slider.setMaximum(100)
        self.text_opacity_slider.valueChanged.connect(self.on_text_opacity_changed)
        self.text_opacity_value_label = QLabel("100%")
        text_opacity_layout.addWidget(text_opacity_label)
        text_opacity_layout.addWidget(self.text_opacity_slider)
        text_opacity_layout.addWidget(self.text_opacity_value_label)

        window_width_layout = QVBoxLayout()
        window_width_label = QLabel("窗口宽度:")
        self.window_width_slider = QSlider(Qt.Orientation.Horizontal)
        self.window_width_slider.setMinimum(400)
        self.window_width_slider.setMaximum(1600)
        self.window_width_slider.valueChanged.connect(self.on_window_width_changed)
        self.window_width_value_label = QLabel("1080 px")
        window_width_layout.addWidget(window_width_label)
        window_width_layout.addWidget(self.window_width_slider)
        window_width_layout.addWidget(self.window_width_value_label)

        offset_layout = QVBoxLayout()
        offset_label = QLabel("歌词时间校准 (秒):")
        offset_hint = QLabel("关闭渐变时：负数=提前显示，正数=延后显示")
        offset_hint.setStyleSheet("color: gray; font-size: 10px;")
        self.offset_slider = QSlider(Qt.Orientation.Horizontal)
        self.offset_slider.setMinimum(-20)
        self.offset_slider.setMaximum(20)
        self.offset_slider.valueChanged.connect(self.on_offset_changed)
        self.offset_value_label = QLabel("+1.0 秒")
        offset_layout.addWidget(offset_label)
        offset_layout.addWidget(offset_hint)
        offset_layout.addWidget(self.offset_slider)
        offset_layout.addWidget(self.offset_value_label)

        ui_layout.addLayout(color_layout)
        ui_layout.addSpacing(20)
        ui_layout.addLayout(border_opacity_layout)
        ui_layout.addSpacing(10)
        ui_layout.addLayout(text_opacity_layout)
        ui_layout.addSpacing(10)
        ui_layout.addLayout(window_width_layout)
        ui_layout.addSpacing(20)
        ui_layout.addLayout(offset_layout)
        ui_layout.addStretch()
        ui_tab.setLayout(ui_layout)

        func_tab = QWidget()
        func_layout = QVBoxLayout()
        self.fade_checkbox = QCheckBox("启用歌词渐入渐出（使用 0.5 秒做切换）")
        self.fade_checkbox.setFont(QFont("Microsoft YaHei", 12))
        self.fade_checkbox.toggled.connect(self.fade_enabled_changed.emit)
        func_layout.addStretch()
        func_layout.addWidget(self.fade_checkbox, alignment=Qt.AlignmentFlag.AlignCenter)
        func_layout.addStretch()
        func_tab.setLayout(func_layout)

        tabs.addTab(ui_tab, "界面")
        tabs.addTab(func_tab, "功能")

        main_layout = QVBoxLayout()
        main_layout.addWidget(tabs)
        self.setLayout(main_layout)

        self.current_color = QColor(255, 255, 255)

    def choose_color(self):
        """选择颜色"""
        color = QColorDialog.getColor(self.current_color, self, "选择字体颜色")
        if color.isValid():
            self.current_color = color
            self.color_changed.emit(color)

    def on_border_opacity_changed(self, value):
        """背景框透明度改变"""
        opacity = value / 100.0
        self.border_opacity_value_label.setText(f"{value}%")
        self.border_opacity_changed.emit(opacity)

    def on_text_opacity_changed(self, value):
        """文字透明度改变"""
        opacity = value / 100.0
        self.text_opacity_value_label.setText(f"{value}%")
        self.text_opacity_changed.emit(opacity)

    def on_offset_changed(self, value):
        """歌词延迟改变"""
        offset = value / 10.0
        self.offset_value_label.setText(f"{offset:+.1f} 秒")
        self.lyric_offset_changed.emit(offset)

    def on_window_width_changed(self, value):
        """窗口宽度改变"""
        self.window_width_value_label.setText(f"{value} px")
        self.window_width_changed.emit(value)


class SupportDialog(QDialog):
    """支持与联系对话框"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("支持与联系")
        self.setMinimumSize(400, 500)

        layout = QVBoxLayout()

        info_label = QLabel("开发者邮箱：1531516107@qq.com")
        info_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        info_label.setFont(QFont("Microsoft YaHei", 12))
        layout.addWidget(info_label)

        layout.addSpacing(20)

        dashang_path = Path(__file__).parent / "dashang.png"
        if dashang_path.exists():
            pixmap = QPixmap(str(dashang_path))
            pixmap = pixmap.scaled(300, 300, Qt.AspectRatioMode.KeepAspectRatio,
                                   Qt.TransformationMode.SmoothTransformation)

            image_label = QLabel()
            image_label.setPixmap(pixmap)
            image_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
            layout.addWidget(image_label)
        else:
            no_image_label = QLabel("(打赏码图片未找到)")
            no_image_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
            layout.addWidget(no_image_label)

        layout.addStretch()
        self.setLayout(layout)


class MainApplication(QApplication):
    """主应用程序"""

    def __init__(self, argv):
        super().__init__(argv)

        self.settings_manager = SettingsManager()
        self.lyric_window = DesktopLyricWindow(self.settings_manager)
        self.lyric_window.show()

        self.create_tray_icon()

        self.backend_thread = BackendEngine()
        
        # 启动时读取持久化配置给后端引擎
        self.backend_thread.set_lyric_offset(self.settings_manager.settings['lyric_offset'])
        self.backend_thread.set_fade_enabled(self.settings_manager.settings['fade_enabled'])
        
        self.backend_thread.lyric_updated.connect(self.on_lyric_updated)
        self.backend_thread.song_changed.connect(self.on_song_changed)
        self.backend_thread.status_changed.connect(self.on_status_changed)
        self.backend_thread.start()

        self.settings_dialog = None
        self.support_dialog = None

    def create_tray_icon(self):
        """创建系统托盘图标"""
        icon_path = Path(__file__).parent / "logo.png"
        if icon_path.exists():
            icon = QIcon(str(icon_path))
        else:
            icon = self.style().standardIcon(self.style().StandardPixmap.SP_MediaPlay)

        self.tray_icon = QSystemTrayIcon(icon, self)
        tray_menu = QMenu()

        self.show_action = QAction("显示歌词", self)
        self.show_action.setCheckable(True)
        self.show_action.setChecked(True)
        self.show_action.triggered.connect(self.toggle_lyric_window)
        tray_menu.addAction(self.show_action)

        self.lock_action = QAction("锁定歌词", self)
        self.lock_action.setCheckable(True)
        self.lock_action.setChecked(False)
        self.lock_action.triggered.connect(self.toggle_lock)
        tray_menu.addAction(self.lock_action)

        tray_menu.addSeparator()

        settings_action = QAction("设置", self)
        settings_action.triggered.connect(self.show_settings)
        tray_menu.addAction(settings_action)

        support_action = QAction("支持/联系作者", self)
        support_action.triggered.connect(self.show_support)
        tray_menu.addAction(support_action)

        tray_menu.addSeparator()

        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self.quit_application)
        tray_menu.addAction(quit_action)

        self.tray_icon.setContextMenu(tray_menu)
        self.tray_icon.show()

    def toggle_lyric_window(self, checked):
        if checked:
            self.lyric_window.show()
        else:
            self.lyric_window.hide()

    def toggle_lock(self, checked):
        self.lyric_window.set_locked(checked)

    def show_settings(self):
        """显示设置对话框并打通数据持久化"""
        if self.settings_dialog is None:
            self.settings_dialog = SettingsDialog(self.lyric_window)
            
            # --- 1. 读取存档，初始化设置面板的滑块和状态 ---
            s = self.settings_manager.settings
            self.settings_dialog.current_color = QColor(s['text_color'])
            self.settings_dialog.border_opacity_slider.setValue(int(s['border_opacity'] * 100))
            self.settings_dialog.text_opacity_slider.setValue(int(s['text_opacity'] * 100))
            self.settings_dialog.offset_slider.setValue(int(s['lyric_offset'] * 10))
            self.settings_dialog.window_width_slider.setValue(s['window_width'])
            self.settings_dialog.fade_checkbox.setChecked(s['fade_enabled'])

            # --- 2. 绑定 UI 实时生效事件 ---
            self.settings_dialog.color_changed.connect(self.lyric_window.update_font_style)
            self.settings_dialog.border_opacity_changed.connect(self.lyric_window.set_border_opacity)
            self.settings_dialog.text_opacity_changed.connect(self.lyric_window.set_text_opacity)
            self.settings_dialog.lyric_offset_changed.connect(self.backend_thread.set_lyric_offset)
            self.settings_dialog.fade_enabled_changed.connect(self.lyric_window.set_fade_enabled)
            self.settings_dialog.fade_enabled_changed.connect(self.backend_thread.set_fade_enabled)
            self.settings_dialog.window_width_changed.connect(self.lyric_window.set_window_width)

            # --- 3. 绑定 JSON 本地持久化保存事件 ---
            self.settings_dialog.color_changed.connect(lambda c: self.settings_manager.save('text_color', c.name()))
            self.settings_dialog.border_opacity_changed.connect(lambda v: self.settings_manager.save('border_opacity', v))
            self.settings_dialog.text_opacity_changed.connect(lambda v: self.settings_manager.save('text_opacity', v))
            self.settings_dialog.lyric_offset_changed.connect(lambda v: self.settings_manager.save('lyric_offset', v))
            self.settings_dialog.window_width_changed.connect(lambda v: self.settings_manager.save('window_width', v))
            self.settings_dialog.fade_enabled_changed.connect(lambda v: self.settings_manager.save('fade_enabled', v))

        self.settings_dialog.show()
        self.settings_dialog.raise_()
        self.settings_dialog.activateWindow()

    def show_support(self):
        if self.support_dialog is None:
            self.support_dialog = SupportDialog(self.lyric_window)

        self.support_dialog.show()
        self.support_dialog.raise_()
        self.support_dialog.activateWindow()

    def quit_application(self):
        self.backend_thread.stop()
        self.backend_thread.wait()
        self.quit()

    def on_lyric_updated(self, text: str):
        self.lyric_window.update_lyric(text)

    def on_song_changed(self, title: str, artist: str):
        pass 
        self.tray_icon.setToolTip(f"正在播放: {title} - {artist}")

    def on_status_changed(self, status: str):
        if status == "paused":
            self.lyric_window.update_lyric("[已暂停]")
        elif status == "stopped":
            self.lyric_window.update_lyric("等待播放...")


def main():
    app = MainApplication(sys.argv)
    sys.exit(app.exec())


if __name__ == "__main__":
    main()