"""
Apple Music 进程接管与内存扫描
需要管理员权限运行
"""

import psutil
import pymem
import sys
import ctypes
from ctypes import wintypes
from typing import Optional, List, Tuple

# Windows API 常量
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100


def find_apple_music_process() -> Optional[int]:
    """查找 Apple Music 进程 ID"""
    target_names = ['AppleMusic.exe', 'Music.exe', 'AppleMusicWin.exe']

    for proc in psutil.process_iter(['pid', 'name']):
        try:
            if proc.info['name'] in target_names:
                print(f"[+] 找到进程: {proc.info['name']} (PID: {proc.info['pid']})")
                return proc.info['pid']
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue

    return None


def attach_process(pid: int) -> Optional[pymem.Pymem]:
    """附加到目标进程"""
    try:
        pm = pymem.Pymem()
        pm.open_process_from_id(pid)
        print(f"[+] 成功附加到进程 {pid}")
        return pm
    except pymem.exception.ProcessNotFound:
        print(f"[-] 进程 {pid} 不存在")
    except pymem.exception.CouldNotOpenProcess:
        print(f"[-] 权限不足，无法打开进程 {pid}")
        print("[!] 请以管理员身份运行此脚本")
    except Exception as e:
        print(f"[-] 附加失败: {e}")

    return None


def scan_memory_for_pattern(pm: pymem.Pymem, pattern: bytes, max_results: int = 50) -> List[Tuple[int, bytes]]:
    """扫描进程内存，查找指定字节模式"""

    # Windows API 结构（64位）
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

    results = []
    scanned_regions = 0
    total_size = 0

    print(f"[*] 搜索模式: {pattern.hex()} ({len(pattern)} 字节)")
    print(f"[*] 开始扫描内存...\n")

    kernel32 = ctypes.windll.kernel32
    address = 0
    max_address = 0x7FFFFFFFFFFF

    while address < max_address:
        mbi = MEMORY_BASIC_INFORMATION64()

        # 查询内存区域
        ret = kernel32.VirtualQueryEx(
            pm.process_handle,
            ctypes.c_ulonglong(address),
            ctypes.byref(mbi),
            ctypes.sizeof(mbi)
        )

        if ret == 0:
            address += 0x10000  # 跳过 64KB
            continue

        # 只扫描已提交的可读内存
        if (mbi.State == MEM_COMMIT and
            mbi.Protect != PAGE_NOACCESS and
            not (mbi.Protect & PAGE_GUARD) and
            mbi.RegionSize >= len(pattern)):

            try:
                # 读取内存
                data = pm.read_bytes(mbi.BaseAddress, mbi.RegionSize)
                scanned_regions += 1
                total_size += mbi.RegionSize

                # 搜索模式
                offset = 0
                while True:
                    pos = data.find(pattern, offset)
                    if pos == -1:
                        break

                    match_addr = mbi.BaseAddress + pos

                    # 提取上下文
                    ctx_start = max(0, pos - 128)
                    ctx_end = min(len(data), pos + len(pattern) + 128)
                    context = data[ctx_start:ctx_end]

                    results.append((match_addr, context))
                    print(f"[+] 匹配 #{len(results)}: 0x{match_addr:016X}")

                    if len(results) >= max_results:
                        print(f"\n[!] 已达到最大结果数 {max_results}")
                        print(f"[*] 扫描了 {scanned_regions} 个区域，共 {total_size / 1024 / 1024:.1f} MB")
                        return results

                    offset = pos + 1

            except Exception:
                pass

        # 移动到下一个区域
        address = mbi.BaseAddress + mbi.RegionSize

    print(f"\n[*] 扫描完成: {scanned_regions} 个区域，共 {total_size / 1024 / 1024:.1f} MB")
    return results


def search_lyrics(pm: pymem.Pymem, lyrics_text: str):
    """搜索歌词字符串"""
    print(f"\n[*] 搜索歌词: '{lyrics_text}'")

    # 转换为 UTF-16LE
    pattern = lyrics_text.encode('utf-16le')

    # 扫描内存
    results = scan_memory_for_pattern(pm, pattern)

    if not results:
        print("\n[-] 未找到匹配")
        return

    print(f"\n{'='*70}")
    print(f"找到 {len(results)} 个匹配位置")
    print(f"{'='*70}\n")

    for i, (addr, context) in enumerate(results, 1):
        print(f"结果 #{i}:")
        print(f"  地址: 0x{addr:016X}")

        # 尝试解码为 UTF-16LE
        try:
            null_pos = context.find(b'\x00\x00')
            if null_pos > 0 and null_pos % 2 == 0:
                text = context[:null_pos].decode('utf-16le', errors='ignore')
            else:
                text = context.decode('utf-16le', errors='ignore')

            if len(text) > 150:
                text = text[:150] + "..."

            print(f"  内容: {text}")
        except:
            print(f"  内容: [解码失败]")

        print()


def main():
    print("[*] Apple Music 内存扫描工具")
    print("[*] 正在搜索进程...")

    # 查找进程
    pid = find_apple_music_process()
    if not pid:
        print("[-] 未找到 Apple Music 进程")
        print("[!] 请确保 Apple Music 正在运行")
        return 1

    # 附加进程
    pm = attach_process(pid)
    if not pm:
        print("\n[!] UWP 应用绕过建议:")
        print("  1. 使用 PsExec 以 SYSTEM 权限运行:")
        print("     PsExec.exe -s -i python process_attach.py")
        print("  2. 禁用 UWP 应用保护 (需重启):")
        print("     reg add HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management /v FeatureSettingsOverride /t REG_DWORD /d 3 /f")
        return 1

    print(f"[+] 进程基址: 0x{pm.process_base.lpBaseOfDll:X}")

    # 交互式歌词搜索
    print("\n" + "="*60)
    print("歌词扫描模式")
    print("="*60)

    while True:
        lyrics = input("\n请输入当前播放的歌词 (输入 q 退出): ").strip()

        if lyrics.lower() == 'q':
            break

        if not lyrics:
            print("[-] 请输入有效的歌词")
            continue

        search_lyrics(pm, lyrics)

    pm.close_process()
    print("\n[+] 进程已分离")
    return 0


if __name__ == "__main__":
    if not psutil.WINDOWS:
        print("[-] 此脚本仅支持 Windows")
        sys.exit(1)

    sys.exit(main())
