"""
Apple Music 歌词提取器
从内存中提取 TTML 格式的歌词数据
"""

import pymem
import psutil
import ctypes
from ctypes import wintypes
import re
from typing import Optional, List, Tuple
from dataclasses import dataclass


# Windows API 常量
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100


@dataclass
class LyricLine:
    """歌词行"""
    begin: float  # 开始时间（秒）
    end: float    # 结束时间（秒）
    text: str     # 歌词文本


def find_apple_music_process() -> Optional[int]:
    """查找 Apple Music 进程"""
    for proc in psutil.process_iter(['pid', 'name']):
        try:
            if proc.info['name'] in ['AppleMusic.exe', 'Music.exe']:
                print(f"[+] 找到进程: {proc.info['name']} (PID: {proc.info['pid']})")
                return proc.info['pid']
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    return None


def attach_process(pid: int) -> Optional[pymem.Pymem]:
    """附加到进程"""
    try:
        pm = pymem.Pymem()
        pm.open_process_from_id(pid)
        print(f"[+] 成功附加到进程 {pid}")
        return pm
    except Exception as e:
        print(f"[-] 附加失败: {e}")
        return None


def find_all_ttml_in_memory(pm: pymem.Pymem) -> List[Tuple[int, bytes]]:
    """在内存中查找所有 TTML 歌词数据"""

    # 搜索特征：<tt xmlns 的 UTF-16LE 编码（TTML 文档开头）
    pattern = '<tt xmlns'.encode('utf-16le')

    print(f"[*] 搜索所有 TTML 数据块...")

    # Windows API 结构
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
    results = []

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

        # 只扫描已提交的可读内存
        if (mbi.State == MEM_COMMIT and
            mbi.Protect != PAGE_NOACCESS and
            not (mbi.Protect & PAGE_GUARD) and
            mbi.RegionSize >= len(pattern)):

            try:
                data = pm.read_bytes(mbi.BaseAddress, mbi.RegionSize)

                # 查找所有匹配
                offset = 0
                while True:
                    pos = data.find(pattern, offset)
                    if pos == -1:
                        break

                    match_addr = mbi.BaseAddress + pos

                    # 向前后各读取 500KB（TTML 文档可能很大）
                    start_addr = max(mbi.BaseAddress, match_addr - 500000)
                    read_size = min(1000000, mbi.BaseAddress + mbi.RegionSize - start_addr)

                    ttml_data = pm.read_bytes(start_addr, read_size)
                    results.append((match_addr, ttml_data))

                    print(f"[+] 找到 TTML #{len(results)}: 0x{match_addr:016X}")

                    offset = pos + 1

            except Exception:
                pass

        address = mbi.BaseAddress + mbi.RegionSize

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

        # 只扫描已提交的、可读的内存块
        if (mbi.State == MEM_COMMIT and
            mbi.Protect != PAGE_NOACCESS and
            not (mbi.Protect & PAGE_GUARD) and
            mbi.RegionSize >= len(pattern)):

            try:
                # 先把当前合法的内存块整个读出来，作为“搜索底片”
                data = pm.read_bytes(mbi.BaseAddress, mbi.RegionSize)

                offset = 0
                while True:
                    # 在底片中寻找特征码（比如 begin= 或者 </p>）
                    pos = data.find(pattern, offset)
                    if pos == -1:
                        break

                    # 换算成进程的绝对物理地址
                    match_addr = mbi.BaseAddress + pos

                    # 从 TTML 开头向后读取 2MB（确保获取完整文档）
                    try:
                        # 直接从匹配位置（<tt xmlns）开始读取 2MB
                        ttml_data = pm.read_bytes(match_addr, 2 * 1024 * 1024)
                        results.append((match_addr, ttml_data))
                        print(f"[+] 找到 TTML #{len(results)}: 0x{match_addr:016X} (读取 2MB)")
                    except Exception:
                        # 如果 2MB 太大，尝试读取到内存区域末尾
                        safe_size = min(1024 * 1024, (mbi.BaseAddress + mbi.RegionSize) - match_addr)
                        if safe_size > 0:
                            ttml_data = pm.read_bytes(match_addr, safe_size)
                            results.append((match_addr, ttml_data))
                            print(f"[+] 找到 TTML #{len(results)}: 0x{match_addr:016X} (读取 {safe_size // 1024}KB)")

                    # 跳过已处理的数据
                    offset = pos + 10000 

            except Exception:
                pass

        address = mbi.BaseAddress + mbi.RegionSize
    return results


def extract_song_info(text: str) -> Tuple[str, str, str]:
    """从 TTML 中提取歌曲信息，返回 (歌曲名, 歌手, 预览)"""
    # 提取歌曲名（可能在 metadata 或其他位置）
    # 尝试多种模式
    song_patterns = [
        r'<title>([^<]+)</title>',
        r'<ttm:title>([^<]+)</ttm:title>',
        r'itunes:songTitle="([^"]+)"',
    ]
    song_name = "未知歌曲"
    for pattern in song_patterns:
        match = re.search(pattern, text)
        if match:
            song_name = match.group(1)
            break

    # 提取歌手名
    artist_match = re.search(r'<ttm:name[^>]*>([^<]+)</ttm:name>', text)
    artist = artist_match.group(1) if artist_match else "未知歌手"

    # 提取第一句歌词作为预览
    first_lyric_match = re.search(r'<p\s+begin="[\d.]+"\s+end="[\d.]+"\s+[^>]*>([^<]+)</p>', text)
    preview = first_lyric_match.group(1)[:40] if first_lyric_match else "无歌词"

    return song_name, artist, preview

def parse_time(time_str: str) -> float:
    """处理 '22.186' 或 '1:03.841' 格式的时间戳"""
    if ':' in time_str:
        parts = time_str.split(':')
        return int(parts[0]) * 60 + float(parts[1])
    return float(time_str)

def parse_ttml_lyrics(data: bytes, save_raw: bool = False) -> Tuple[List[LyricLine], str, str, str]:
    """解析 TTML 格式的歌词，返回 (歌词列表, 歌曲名, 歌手, 预览)"""

    # 解码为 UTF-16LE
    try:
        text = data.decode('utf-16le', errors='ignore')
    except:
        return [], "解码失败", "", ""

    # 提取歌曲信息
    song_name, artist, preview = extract_song_info(text)

    # 保存原始数据用于调试
    if save_raw:
        with open('ttml_raw.txt', 'w', encoding='utf-8') as f:
            f.write(text)
        print("[+] 原始数据已保存到 ttml_raw.txt")

    # 正则提取歌词行
    pattern = r'<p\s+begin="([\d:.]+)"\s+end="([\d:.]+)"[^>]*>([^<]+)</p>'
    matches = re.findall(pattern, text)

    lyrics = []
    for begin_str, end_str, lyric_text in matches:
        try:
            # 【修改：调用我们新建的 parse_time 函数】
            begin = parse_time(begin_str)
            end = parse_time(end_str)
            
            # 清理 HTML 转义字符（比如 &apos;）
            lyric_text = lyric_text.strip().replace('&apos;', "'")
            
            if lyric_text:
                lyrics.append(LyricLine(begin, end, lyric_text))
        except ValueError:
            continue

    lyrics.sort(key=lambda x: x.begin)

    return lyrics, song_name, artist, preview


def main():
    print("[*] Apple Music 歌词提取器")
    print("[*] 正在搜索进程...")

    # 查找进程
    pid = find_apple_music_process()
    if not pid:
        print("[-] 未找到 Apple Music 进程")
        return 1

    # 附加进程
    pm = attach_process(pid)
    if not pm:
        return 1

    # 查找所有 TTML 数据
    print("\n[*] 开始扫描内存...")
    all_ttml = find_all_ttml_in_memory(pm)

    if not all_ttml:
        print("[-] 未找到 TTML 歌词数据")
        pm.close_process()
        return 1

    print(f"\n[+] 找到 {len(all_ttml)} 个 TTML 数据块")

    # 解析所有歌词并显示预览
    parsed_songs = []
    for i, (addr, data) in enumerate(all_ttml):
        lyrics, song_name, artist, preview = parse_ttml_lyrics(data, save_raw=(i == 0))
        if lyrics:
            parsed_songs.append((i, addr, lyrics, song_name, artist, preview))

    if not parsed_songs:
        print("[-] 未能解析出任何歌词")
        pm.close_process()
        return 1

    # 显示所有歌曲供用户选择
    print("\n" + "="*70)
    print("找到以下歌曲:")
    print("="*70 + "\n")

    for idx, addr, lyrics, song_name, artist, preview in parsed_songs:
        print(f"[{idx}] {song_name}")
        print(f"    歌手: {artist}")
        print(f"    歌词行数: {len(lyrics)}")
        print(f"    预览: {preview}")
        print()

    # 让用户选择
    while True:
        try:
            choice = input("请选择要提取的歌曲编号 (输入 q 退出): ").strip()
            if choice.lower() == 'q':
                pm.close_process()
                return 0

            choice_idx = int(choice)
            selected = next((s for s in parsed_songs if s[0] == choice_idx), None)

            if selected:
                break
            else:
                print("[-] 无效的编号，请重新选择")
        except ValueError:
            print("[-] 请输入有效的数字")

    _, addr, lyrics, song_name, artist, preview = selected

    # 显示选中的歌词
    print(f"\n[+] 已选择: {song_name} - {artist}")
    print(f"[+] 共 {len(lyrics)} 行歌词\n")
    print("="*70)
    print("歌词内容")
    print("="*70 + "\n")

    for i, lyric in enumerate(lyrics, 1):
        begin_min = int(lyric.begin // 60)
        begin_sec = lyric.begin % 60
        print(f"[{i:2d}] [{begin_min:02d}:{begin_sec:05.2f}] {lyric.text}")

    # 保存为 LRC 格式
    filename = f"{song_name}.lrc"

    with open(filename, 'w', encoding='utf-8') as f:
        f.write(f"[ti:{song_name}]\n")
        f.write(f"[ar:{artist}]\n")
        f.write("\n")
        for lyric in lyrics:
            min_val = int(lyric.begin // 60)
            sec_val = lyric.begin % 60
            f.write(f"[{min_val:02d}:{sec_val:05.2f}]{lyric.text}\n")

    print(f"\n[+] 歌词已保存到 {filename}")

    pm.close_process()
    return 0


if __name__ == "__main__":
    import sys
    sys.exit(main())
