"""
Apple Music 后端引擎 - QThread 版本
封装异步媒体检测和内存扫描逻辑
"""

import asyncio
import re
import ctypes
from ctypes import wintypes
from typing import List, Optional, Tuple
from dataclasses import dataclass
import pymem
import psutil
from PySide6.QtCore import QThread, Signal

try:
    import uiautomation as auto
    UIAUTOMATION_AVAILABLE = True
except ImportError:
    UIAUTOMATION_AVAILABLE = False

# Windows Media Control
try:
    from winsdk.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus
    )
    WINSDK_AVAILABLE = True
except ImportError:
    WINSDK_AVAILABLE = False


# Windows API 常量
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100


@dataclass
class LyricLine:
    """歌词行"""
    begin: float
    end: float
    text: str


class BackendEngine(QThread):
    """后端引擎线程"""

    lyric_updated = Signal(str)
    song_changed = Signal(str, str)
    status_changed = Signal(str)

    def __init__(self):
        super().__init__()
        self.running = True
        self.current_lyrics = []
        self.last_played_song = ""
        self.last_index = -1
        self.last_status = None
        self.lyric_offset = 0.5
        self.fade_enabled = True

    def set_lyric_offset(self, offset: float):
        """设置歌词时间偏移量"""
        self.lyric_offset = offset

    def set_fade_enabled(self, enabled: bool):
        """设置是否启用渐变效果"""
        self.fade_enabled = enabled

    def stop(self):
        """停止线程"""
        self.running = False

    def run(self):
        """线程入口"""
        if not WINSDK_AVAILABLE:
            self.status_changed.emit("winsdk_not_available")
            return

        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)

        try:
            loop.run_until_complete(self.sync_engine())
        finally:
            loop.close()

    def find_apple_music_process(self) -> Optional[int]:
        """查找 Apple Music 进程"""
        for proc in psutil.process_iter(['pid', 'name']):
            try:
                if proc.info['name'] in ['AppleMusic.exe', 'Music.exe']:
                    return proc.info['pid']
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
        return None

    def extract_all_ttml(self, pid: int) -> List[bytes]:
        """从内存中提取所有 TTML 数据块，物理截断到 </tt> 闭合标签"""
        try:
            pm = pymem.Pymem()
            pm.open_process_from_id(pid)
        except Exception:
            return []

        pattern = '<tt xmlns'.encode('utf-16le')
        end_tag = '</tt>'.encode('utf-16le')
        ttml_list = []

        class MEMORY_BASIC_INFORMATION64(ctypes.Structure):
            _fields_ = [
                ("BaseAddress", ctypes.c_ulonglong),
                ("AllocationBase", ctypes.c_ulonglong),
                ("AllocationProtect", wintypes.DWORD),
                ("__alignment1", wintypes.DWORD),
                ("RegionSize", ctypes.c_ulonglong),
                ("State", wintypes.DWORD),
                ("Protect", wintypes.DWORD),
                ("Type", wintypes.DWORD),
                ("__alignment2", wintypes.DWORD),
            ]

        kernel32 = ctypes.windll.kernel32
        address = 0
        max_address = 0x7FFFFFFFFFFF

        try:
            while address < max_address:
                mbi = MEMORY_BASIC_INFORMATION64()
                ret = kernel32.VirtualQueryEx(
                    pm.process_handle,
                    ctypes.c_ulonglong(address),
                    ctypes.byref(mbi),
                    ctypes.sizeof(mbi)
                )

                if ret == 0:
                    address += 0x10000
                    continue

                if (mbi.State == MEM_COMMIT and
                    mbi.Protect != PAGE_NOACCESS and
                    not (mbi.Protect & PAGE_GUARD) and
                    mbi.RegionSize >= len(pattern)):
                    try:
                        data = pm.read_bytes(mbi.BaseAddress, mbi.RegionSize)
                        offset = 0
                        # 【修复：恢复 While 循环，榨干这块内存里的每一首歌】
                        while True:
                            pos = data.find(pattern, offset)
                            if pos == -1:
                                break
                            
                            match_addr = mbi.BaseAddress + pos
                            try:
                                ttml_data = pm.read_bytes(match_addr, 2 * 1024 * 1024)
                            except Exception:
                                safe_size = min(1024 * 1024, (mbi.BaseAddress + mbi.RegionSize) - match_addr)
                                if safe_size > 0:
                                    ttml_data = pm.read_bytes(match_addr, safe_size)
                                else:
                                    ttml_data = None

                            if ttml_data:
                                end_pos = ttml_data.find(end_tag)
                                if end_pos != -1:
                                    # 完美截断这首歌
                                    extracted_chunk = ttml_data[:end_pos + len(end_tag)]
                                    ttml_list.append(extracted_chunk)
                                    # 重点：步长跳过这首歌，继续往后挖新歌！
                                    offset = pos + end_pos + len(end_tag)
                                else:
                                    offset = pos + 10000
                            else:
                                offset = pos + 10000
                    except Exception:
                        pass

                address = mbi.BaseAddress + mbi.RegionSize
        finally:
            pm.close_process()

        return ttml_list

    # def trigger_lyrics_button(self, pid: int) -> bool:
    #     """用 UIAutomation 后台静默触发歌词按钮，避免移动物理鼠标。"""
    #     if not UIAUTOMATION_AVAILABLE:
    #         return False

    #     try:
    #         auto.SetGlobalSearchTimeout(2)
    #         root = auto.GetRootControl()
    #         windows = root.GetChildren()
    #     except Exception:
    #         return False

    #     target_window = None
    #     for win in windows:
    #         try:
    #             process_id = getattr(win, 'ProcessId', None)
    #             name = (getattr(win, 'Name', '') or '').lower()
    #             if process_id == pid or 'apple music' in name:
    #                 target_window = win
    #                 break
    #         except Exception:
    #             continue

    #     if not target_window:
    #         return False

    #     keywords = ('lyrics', '歌词', 'lyric')
    #     automation_keywords = ('lyrics', 'lyric')

    #     def walk(control, depth=0, max_depth=8):
    #         if depth > max_depth:
    #             return None
    #         try:
    #             children = control.GetChildren()
    #         except Exception:
    #             return None

    #         for child in children:
    #             try:
    #                 control_type = (getattr(child, 'ControlTypeName', '') or '').lower()
    #                 name = (getattr(child, 'Name', '') or '').lower()
    #                 automation_id = (getattr(child, 'AutomationId', '') or '').lower()
    #                 is_button = 'button' in control_type or child.__class__.__name__ == 'ButtonControl'
    #                 if is_button and (
    #                     any(k in name for k in keywords) or
    #                     any(k in automation_id for k in automation_keywords)
    #                 ):
    #                     return child
    #             except Exception:
    #                 pass

    #             found = walk(child, depth + 1, max_depth)
    #             if found:
    #                 return found
    #         return None

    #     button = walk(target_window)
    #     if not button:
    #         return False

    #     try:
    #         if hasattr(button, 'Invoke'):
    #             button.Invoke()
    #         else:
    #             button.Click(simulateMove=False)
    #         return True
    #     except Exception:
    #         try:
    #             button.Click(simulateMove=False)
    #             return True
    #         except Exception:
    #             return False

    def trigger_lyrics_button(self, pid: int) -> bool:
        """用原生 UIAutomation 极速搜索，无视层级深度"""
        if not UIAUTOMATION_AVAILABLE:
            return False

        try:
            auto.SetGlobalSearchTimeout(2)
            # 定位主窗口
            window = auto.WindowControl(searchDepth=2, ProcessId=pid)
            if not window.Exists(0):
                window = auto.WindowControl(searchDepth=2, RegexName='(?i).*apple music.*')

            if not window.Exists(0):
                return False

            # WinUI 3 树极深，使用深度为 20 的原生底层搜索
            button = window.Control(searchDepth=20, RegexName='(?i).*(lyrics|歌词).*')
            if not button.Exists(0):
                return False

            # 【防误触核心】：如果已经是高亮打开状态，绝对不要点，点了反而关掉加载！
            try:
                if button.GetTogglePattern().ToggleState == 1:
                    return True # 已经是开启状态，直接返回等网速
            except Exception:
                pass

            try:
                button.Invoke()
            except Exception:
                button.Click(simulateMove=False)
            return True
        except Exception as e:
            print(f"UIAutomation 触发失败: {e}")
            return False
        
        
    def parse_time(self, time_str: str) -> float:
        """解析时间戳 '22.186' 或 '1:03.841'"""
        time_str = time_str.strip()
        if ':' in time_str:
            parts = time_str.split(':')
            return int(parts[0]) * 60 + float(parts[1])
        return float(time_str)

    def parse_ttml_lyrics(self, data: bytes) -> Tuple[List[LyricLine], float]:
        """解析 TTML 歌词，返回 (歌词列表, TTML标定时长)"""
        try:
            text = data.decode('utf-16le', errors='ignore')
        except Exception:
            return [], 0.0

        # 提取 body dur 属性
        ttml_duration = 0.0
        dur_match = re.search(r'<body[^>]*dur="([\d:.]+)"', text)
        if dur_match:
            try:
                ttml_duration = self.parse_time(dur_match.group(1))
            except Exception:
                pass

        pattern = r'<p\s+begin="([\d:.]+)"\s+end="([\d:.]+)"[^>]*>([^<]+)</p>'
        matches = re.findall(pattern, text)

        lyrics = []
        seen = set()

        for begin_str, end_str, lyric_text in matches:
            try:
                begin = self.parse_time(begin_str)
                end = self.parse_time(end_str)
                lyric_text = lyric_text.strip().replace('&apos;', "'")
                if lyric_text:
                    key = (begin, lyric_text)
                    if key not in seen:
                        seen.add(key)
                        lyrics.append(LyricLine(begin, end, lyric_text))
            except Exception:
                continue

        lyrics.sort(key=lambda x: x.begin)
        return lyrics, ttml_duration

    async def get_media_info(self, manager) -> Optional[Tuple[str, str, float, str, float]]:
        """获取媒体信息：(title, artist, position, status, duration)"""
        try:
            if not manager:
                return None

            session = manager.get_current_session()
            if not session:
                return None

            playback_info = session.get_playback_info()
            status = playback_info.playback_status

            if status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.PLAYING:
                status_str = 'playing'
            elif status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.PAUSED:
                status_str = 'paused'
            else:
                status_str = 'stopped'

            media_properties = await session.try_get_media_properties_async()
            if not media_properties:
                return None

            title = media_properties.title or '未知'
            artist = media_properties.artist or '未知'

            timeline = session.get_timeline_properties()
            if timeline and timeline.position:
                position_seconds = timeline.position.total_seconds()
            else:
                position_seconds = 0.0

            if timeline and timeline.end_time:
                duration_seconds = timeline.end_time.total_seconds()
            else:
                duration_seconds = 0.0

            return title, artist, position_seconds, status_str, duration_seconds
        except Exception:
            return None

    def find_current_lyric(self, current_time: float) -> int:
        """找到当前时间对应的歌词索引，严格校验 begin/end，间奏期间返回 -1"""
        adjusted_time = current_time + self.lyric_offset

        for i, lyric in enumerate(self.current_lyrics):
            if lyric.begin <= adjusted_time < lyric.end:
                return i

        return -1

    async def sync_engine(self):
        """主同步引擎"""
        manager = None
        try:
            manager = await GlobalSystemMediaTransportControlsSessionManager.request_async()
        except Exception as e:
            print(f"SMTC 管理器初始化失败: {e}")

        while self.running:
            try:
                media_info = await self.get_media_info(manager)

                if not media_info:
                    if self.last_played_song:
                        self.status_changed.emit("stopped")
                        self.last_played_song = ""
                        self.current_lyrics = []
                        self.last_index = -1
                        self.last_status = None
                    await asyncio.sleep(1)
                    continue

                title, artist, position, status, duration = media_info
                song_key = f"{title}-{artist}"

                if status == "paused":
                    if self.last_status != "paused":
                        self.status_changed.emit("paused")
                        self.last_status = "paused"
                    await asyncio.sleep(0.5)
                    continue

                if self.last_status == "paused":
                    self.status_changed.emit("playing")
                self.last_status = "playing"

                # if song_key != self.last_played_song:
                #     self.song_changed.emit(title, artist)
                #     await asyncio.sleep(1)

                #     pid = self.find_apple_music_process()
                #     if pid:
                #         ttml_list = await asyncio.to_thread(self.extract_all_ttml, pid)
                        
                #         # 【修复：增加多轮等待机制，应对网络拉取延迟】
                #         if not ttml_list:
                #             await asyncio.to_thread(self.trigger_lyrics_button, pid)
                #             # 尝试等待并重新扫描 3 次（共 4.5 秒容错空间）
                #             for _ in range(3):
                #                 await asyncio.sleep(1.5)
                #                 ttml_list = await asyncio.to_thread(self.extract_all_ttml, pid)
                #                 if ttml_list:
                #                     break

                #         if ttml_list:
                #             # 黄金指纹匹配：TTML dur vs SMTC duration
                #             best_match = None

                #             for ttml_data in ttml_list:
                #                 lyrics, ttml_dur = self.parse_ttml_lyrics(ttml_data)
                #                 if not lyrics or ttml_dur == 0.0:
                #                     continue

                #                 # 计算时长误差
                #                 duration_diff = abs(ttml_dur - duration)

                #                 # 误差小于1秒即为完美匹配
                #                 if duration_diff < 1.0:
                #                     best_match = lyrics
                #                     break

                #             if best_match:
                #                 self.current_lyrics = best_match
                #                 self.last_played_song = song_key
                #                 self.last_index = -1

                # if song_key != self.last_played_song:
                #     # 【核心修复 1：立即更新状态，强行打破死锁陷阱！】
                #     self.last_played_song = song_key
                #     self.current_lyrics = []
                #     self.last_index = -1
                    
                #     self.song_changed.emit(title, artist)
                #     # 给用户友好的 UI 交互反馈
                #     self.lyric_updated.emit("正在加载歌词...")

                #     await asyncio.sleep(1)

                #     pid = self.find_apple_music_process()
                #     if pid:
                #         ttml_list = await asyncio.to_thread(self.extract_all_ttml, pid)
                        
                #         if not ttml_list:
                #             await asyncio.to_thread(self.trigger_lyrics_button, pid)
                #             # 重试 3 次
                #             for _ in range(3):
                #                 await asyncio.sleep(1.5)
                #                 ttml_list = await asyncio.to_thread(self.extract_all_ttml, pid)
                #                 if ttml_list:
                #                     break

                #         if ttml_list:
                #             best_match = None

                #             # 【核心修复 2：双轮容错匹配机制】
                #             # 第一轮：黄金指纹精准匹配（放宽到 2.5 秒误差）
                #             for ttml_data in ttml_list:
                #                 lyrics, ttml_dur = self.parse_ttml_lyrics(ttml_data)
                #                 if not lyrics:
                #                     continue
                                
                #                 if ttml_dur > 0 and abs(ttml_dur - duration) <= 2.5:
                #                     best_match = lyrics
                #                     break

                #             # 第二轮：如果第一轮没找到（可能没有 dur 标签），使用最后一句歌词的物理边界兜底
                #             if not best_match:
                #                 for ttml_data in ttml_list:
                #                     lyrics, _ = self.parse_ttml_lyrics(ttml_data)
                #                     # 正常歌词的最后一句，距离歌曲结束一般不会超过 15 秒
                #                     if lyrics and abs(lyrics[-1].end - duration) <= 15.0:
                #                         best_match = lyrics
                #                         break

                #             if best_match:
                #                 self.current_lyrics = best_match
                #                 # 强制清空索引，触发立即更新
                #                 self.last_index = -1 
                #             else:
                #                 self.lyric_updated.emit("未找到同步歌词")

                if song_key != self.last_played_song:
                    self.last_played_song = song_key
                    self.current_lyrics = []
                    self.last_index = -1

                    self.song_changed.emit(title, artist)
                    self.lyric_updated.emit("正在加载歌词...")

                    await asyncio.sleep(1)

                    pid = self.find_apple_music_process()
                    if pid:
                        best_match = None
                        best_score = -999

                        for attempt in range(4):
                            ttml_list = await asyncio.to_thread(self.extract_all_ttml, pid)

                            if ttml_list:
                                for ttml_data in ttml_list:
                                    lyrics, ttml_dur = self.parse_ttml_lyrics(ttml_data)
                                    if not lyrics:
                                        continue

                                    score = 0

                                    if ttml_dur > 0:
                                        duration_diff = abs(ttml_dur - duration)
                                        if duration_diff <= 3.0:
                                            score += 100
                                        else:
                                            score -= 50

                                    try:
                                        text = ttml_data.decode('utf-16le', errors='ignore').lower()
                                        title_chars = set(c for c in title.lower() if c.isalnum())
                                        artist_chars = set(c for c in artist.lower() if c.isalnum())

                                        title_hits = sum(1 for c in title_chars if c in text)
                                        artist_hits = sum(1 for c in artist_chars if c in text)

                                        if title_chars:
                                            score += (title_hits / len(title_chars)) * 150
                                        if artist_chars:
                                            score += (artist_hits / len(artist_chars)) * 150
                                    except Exception:
                                        pass

                                    if score > best_score:
                                        best_score = score
                                        best_match = lyrics

                            if best_match and best_score >= 50:
                                break

                            if attempt == 0:
                                await asyncio.to_thread(self.trigger_lyrics_button, pid)
                            
                            # 等待网络拉取，进入下一轮循环扫内存
                            await asyncio.sleep(1.5)

                        if best_match:
                            self.current_lyrics = best_match
                            self.last_index = -1 
                        else:
                            self.lyric_updated.emit("未找到同步歌词")


                if self.current_lyrics:
                    current_index = self.find_current_lyric(position)
                    if current_index != self.last_index:
                        if current_index >= 0:
                            self.lyric_updated.emit(self.current_lyrics[current_index].text)
                        else:
                            self.lyric_updated.emit("")
                        self.last_index = current_index

                await asyncio.sleep(0.1)

            except Exception as e:
                print(f"Backend engine error: {e}")
                await asyncio.sleep(1)
