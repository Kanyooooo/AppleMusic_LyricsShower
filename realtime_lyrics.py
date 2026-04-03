"""
Apple Music 实时歌词显示器 - 事件驱动版
全自动切歌检测 + 智能 TTML 匹配 + 毫秒级同步
"""

import asyncio
import os
import sys
import re
import ctypes
from ctypes import wintypes
from typing import List, Optional, Tuple
from dataclasses import dataclass
import pymem
import psutil

# Windows Media Control
try:
    from winsdk.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus
    )
    WINSDK_AVAILABLE = True
except ImportError:
    WINSDK_AVAILABLE = False
    print("[-] winsdk 未安装，请运行: pip install winsdk")
    sys.exit(1)


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


class MemoryScanner:
    """内存扫描器"""

    def __init__(self, pid: int):
        self.pm = pymem.Pymem()
        self.pm.open_process_from_id(pid)

    def __del__(self):
        try:
            self.pm.close_process()
        except:
            pass

    def find_all_ttml(self) -> List[Tuple[int, bytes]]:
        """扫描所有 TTML 数据块"""
        pattern = '<tt xmlns'.encode('utf-16le')
        results = []

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

        while address < max_address:
            mbi = MEMORY_BASIC_INFORMATION64()
            ret = kernel32.VirtualQueryEx(
                self.pm.process_handle,
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
                    data = self.pm.read_bytes(mbi.BaseAddress, mbi.RegionSize)
                    offset = 0

                    while True:
                        pos = data.find(pattern, offset)
                        if pos == -1:
                            break

                        match_addr = mbi.BaseAddress + pos

                        # 从 TTML 开头读取 2MB
                        try:
                            ttml_data = self.pm.read_bytes(match_addr, 2 * 1024 * 1024)
                            results.append((match_addr, ttml_data))
                        except:
                            safe_size = min(1024 * 1024, (mbi.BaseAddress + mbi.RegionSize) - match_addr)
                            if safe_size > 0:
                                ttml_data = self.pm.read_bytes(match_addr, safe_size)
                                results.append((match_addr, ttml_data))

                        offset = pos + 10000
                except:
                    pass

            address = mbi.BaseAddress + mbi.RegionSize

        return results


def parse_time(time_str: str) -> float:
    """解析时间戳：'22.186' 或 '1:03.841'"""
    time_str = time_str.strip()
    if ':' in time_str:
        parts = time_str.split(':')
        minutes = int(parts[0])
        seconds = float(parts[1])
        return minutes * 60 + seconds
    return float(time_str)


def extract_song_info(text: str) -> Tuple[str, str]:
    """提取歌名和歌手"""
    # 提取歌手
    artist_match = re.search(r'<ttm:name[^>]*>([^<]+)</ttm:name>', text)
    artist = artist_match.group(1).strip() if artist_match else ""

    # 提取第一句歌词作为特征
    first_lyric_match = re.search(r'<p\s+begin="[\d:.]+"\s+end="[\d:.]+"\s+[^>]*>([^<]+)</p>', text)
    preview = first_lyric_match.group(1).strip()[:50] if first_lyric_match else ""

    return artist, preview


def parse_ttml_lyrics(data: bytes) -> Tuple[List[LyricLine], str, str]:
    """
    解析 TTML，返回 (去重后的歌词, 歌手, 预览)
    """
    try:
        text = data.decode('utf-16le', errors='ignore')
    except:
        return [], "", ""

    # 提取歌曲信息
    artist, preview = extract_song_info(text)

    # 提取歌词（支持 1:03.841 格式）
    pattern = r'<p\s+begin="([\d:.]+)"\s+end="([\d:.]+)"[^>]*>([^<]+)</p>'
    matches = re.findall(pattern, text)

    lyrics = []
    seen = set()  # 去重：(begin, text)

    for begin_str, end_str, lyric_text in matches:
        try:
            begin = parse_time(begin_str)
            end = parse_time(end_str)
            lyric_text = lyric_text.strip().replace('&apos;', "'")

            if lyric_text:
                key = (begin, lyric_text)
                if key not in seen:
                    seen.add(key)
                    lyrics.append(LyricLine(begin, end, lyric_text))
        except Exception as e:
            continue

    lyrics.sort(key=lambda x: x.begin)
    return lyrics, artist, preview


def smart_match_ttml(all_ttml: List[Tuple[int, bytes]], target_title: str, target_artist: str) -> Optional[bytes]:
    """
    智能匹配：从多个 TTML 块中选择最匹配当前歌曲的那一个
    匹配策略：
    1. 优先匹配歌手名
    2. 其次匹配歌词内容（标题可能在歌词中）
    """
    if not all_ttml:
        return None

    candidates = []

    for addr, data in all_ttml:
        lyrics, artist, preview = parse_ttml_lyrics(data)

        if not lyrics:
            continue

        score = 0

        # 歌手名匹配（权重最高）
        if artist and target_artist:
            if artist.lower() in target_artist.lower() or target_artist.lower() in artist.lower():
                score += 100

        # 歌词内容匹配标题
        if target_title and preview:
            # 移除标点符号进行模糊匹配
            title_clean = re.sub(r'[^\w\s]', '', target_title.lower())
            preview_clean = re.sub(r'[^\w\s]', '', preview.lower())

            if title_clean in preview_clean or preview_clean in title_clean:
                score += 50

        candidates.append((score, len(lyrics), data))

    if not candidates:
        return all_ttml[0][1]  # 返回第一个

    # 按分数排序，分数相同时选歌词行数多的
    candidates.sort(key=lambda x: (x[0], x[1]), reverse=True)

    return candidates[0][2]


async def get_media_info() -> Optional[Tuple[str, str, float, str]]:
    """获取系统媒体信息：(歌名, 歌手, 当前播放秒数, 播放状态)"""
    try:
        manager = await GlobalSystemMediaTransportControlsSessionManager.request_async()
        session = manager.get_current_session()

        if not session:
            return None

        # 获取播放状态
        playback_info = session.get_playback_info()
        status = playback_info.playback_status

        if status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.PLAYING:
            status_str = "playing"
        elif status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.PAUSED:
            status_str = "paused"
        else:
            status_str = "stopped"

        # 获取媒体信息
        media_properties = await session.try_get_media_properties_async()
        if not media_properties:
            return None

        title = media_properties.title or ""
        artist = media_properties.artist or ""

        # 获取播放进度
        timeline = session.get_timeline_properties()
        if timeline and timeline.position:
            position_seconds = timeline.position.total_seconds()
        else:
            position_seconds = 0.0

        return title, artist, position_seconds, status_str

    except Exception:
        return None


def find_current_lyric(lyrics: List[LyricLine], current_time: float, offset: float = -0.3) -> int:
    """
    找到当前时间对应的歌词索引
    offset: 歌词提前量（秒），默认提前 0.3 秒
    """
    adjusted_time = current_time + offset

    for i, lyric in enumerate(lyrics):
        if lyric.begin <= adjusted_time < lyric.end:
            return i

    # 如果超过最后一句，返回最后一句
    if lyrics and adjusted_time >= lyrics[-1].begin:
        return len(lyrics) - 1

    return -1


def clear_screen():
    """清屏"""
    os.system('cls' if os.name == 'nt' else 'clear')


def display_lyrics(lyrics: List[LyricLine], current_index: int, song_name: str, artist: str):
    """显示歌词（三行滚动效果）"""
    clear_screen()

    print("=" * 70)
    print(f"♪ {song_name}")
    print(f"  {artist}")
    print("=" * 70)
    print()

    # 显示上一句（灰色）
    if current_index > 0:
        prev_line = lyrics[current_index - 1]
        print(f"\033[90m  {prev_line.text}\033[0m")
    else:
        print()

    # 显示当前句（高亮青色）
    if 0 <= current_index < len(lyrics):
        current_line = lyrics[current_index]
        print(f"\033[1;36m▶ {current_line.text}\033[0m")
    else:
        print()

    # 显示下一句（灰色）
    if current_index < len(lyrics) - 1:
        next_line = lyrics[current_index + 1]
        print(f"\033[90m  {next_line.text}\033[0m")
    else:
        print()

    print()
    print("=" * 70)


async def sync_engine():
    """
    主同步引擎 - 事件驱动
    """
    print("[*] Apple Music 实时歌词引擎")
    print("[*] 正在初始化...\n")

    # 状态追踪变量
    current_lyrics: List[LyricLine] = []
    last_played_song = ""
    last_index = -1
    last_status = None

    while True:
        try:
            # 每 0.1 秒获取一次媒体信息
            media_info = await get_media_info()

            if not media_info:
                if last_played_song:
                    clear_screen()
                    print("\n\n    [无播放内容]\n\n")
                    last_played_song = ""
                    current_lyrics = []
                    last_index = -1
                    last_status = None
                await asyncio.sleep(1)
                continue

            title, artist, position, status = media_info
            song_key = f"{title}-{artist}"

            # 处理暂停状态
            if status == "paused":
                if last_status != "paused":
                    clear_screen()
                    print("\n" + "=" * 70)
                    print(f"♪ {title}")
                    print(f"  {artist}")
                    print("=" * 70)
                    print("\n\n    [已暂停]\n\n")
                    print("=" * 70)
                    last_status = "paused"
                await asyncio.sleep(0.5)
                continue

            last_status = "playing"

            # 切歌检测
            if song_key != last_played_song:
                print(f"\n[*] 检测到切歌，正在同步新歌：{title}")
                print(f"[*] 歌手：{artist}")

                # 给内存一点加载时间
                await asyncio.sleep(1)

                # 查找进程
                pid = None
                for proc in psutil.process_iter(['pid', 'name']):
                    try:
                        if proc.info['name'] in ['AppleMusic.exe', 'Music.exe']:
                            pid = proc.info['pid']
                            break
                    except:
                        continue

                if not pid:
                    print("[-] 未找到 Apple Music 进程")
                    await asyncio.sleep(2)
                    continue

                # 扫描所有 TTML
                print("[*] 正在扫描内存...")
                try:
                    scanner = MemoryScanner(pid)
                    all_ttml = scanner.find_all_ttml()

                    if not all_ttml:
                        print("[-] 未找到 TTML 数据")
                        await asyncio.sleep(2)
                        continue

                    print(f"[+] 找到 {len(all_ttml)} 个 TTML 数据块")

                    # 智能匹配当前歌曲
                    print("[*] 正在智能匹配...")
                    matched_ttml = smart_match_ttml(all_ttml, title, artist)

                    if not matched_ttml:
                        print("[-] 匹配失败")
                        await asyncio.sleep(2)
                        continue

                    # 解析歌词
                    lyrics, _, _ = parse_ttml_lyrics(matched_ttml)

                    if not lyrics:
                        print("[-] 歌词解析失败")
                        await asyncio.sleep(2)
                        continue

                    current_lyrics = lyrics
                    last_played_song = song_key
                    last_index = -1

                    print(f"[+] 成功加载 {len(current_lyrics)} 行歌词")
                    print()

                except Exception as e:
                    print(f"[-] 错误: {e}")
                    await asyncio.sleep(2)
                    continue

            # 毫秒级歌词同步
            if current_lyrics:
                current_index = find_current_lyric(current_lyrics, position, offset=-0.3)

                # 歌词切换时更新显示
                if current_index != last_index and current_index >= 0:
                    display_lyrics(current_lyrics, current_index, title, artist)
                    last_index = current_index

            await asyncio.sleep(0.1)

        except KeyboardInterrupt:
            clear_screen()
            print("\n[*] 已退出")
            break
        except Exception as e:
            print(f"[-] 引擎错误: {e}")
            await asyncio.sleep(1)


def main():
    """入口函数"""
    if not WINSDK_AVAILABLE:
        return 1

    try:
        asyncio.run(sync_engine())
    except KeyboardInterrupt:
        print("\n[*] 已退出")

    return 0


if __name__ == "__main__":
    sys.exit(main())
