"""
底层内存扫描器 - 使用 Windows API 直接扫描
模仿 Cheat Engine 的扫描方式
"""

import ctypes
from ctypes import wintypes
import sys

# Windows API 结构和常量
class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wintypes.DWORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
    ]

# 常量
PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

kernel32 = ctypes.windll.kernel32
OpenProcess = kernel32.OpenProcess
VirtualQueryEx = kernel32.VirtualQueryEx
ReadProcessMemory = kernel32.ReadProcessMemory
CloseHandle = kernel32.CloseHandle


def scan_process_memory(pid: int, pattern: bytes, max_results: int = 50):
    """使用底层 API 扫描进程内存"""

    # 打开进程
    h_process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h_process:
        print(f"[-] 无法打开进程 {pid}，错误码: {ctypes.get_last_error()}")
        return []

    print(f"[+] 成功打开进程 {pid}")
    print(f"[*] 搜索模式: {pattern.hex()} (长度: {len(pattern)} 字节)")
    print(f"[*] 开始扫描整个地址空间...\n")

    results = []
    address = 0
    max_address = 0x7FFFFFFFFFFF  # 64位地址空间上限
    mbi = MEMORY_BASIC_INFORMATION()
    scanned_regions = 0
    total_bytes = 0

    try:
        while address < max_address:
            # 查询内存区域信息
            if VirtualQueryEx(h_process, ctypes.c_void_p(address), ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
                break

            # 检查是否是已提交的可读内存
            if (mbi.State == MEM_COMMIT and
                mbi.Protect not in (PAGE_NOACCESS, PAGE_GUARD) and
                mbi.RegionSize >= len(pattern)):

                # 读取内存
                buffer = ctypes.create_string_buffer(mbi.RegionSize)
                bytes_read = ctypes.c_size_t(0)

                if ReadProcessMemory(h_process, ctypes.c_void_p(mbi.BaseAddress),
                                    buffer, mbi.RegionSize, ctypes.byref(bytes_read)):

                    scanned_regions += 1
                    total_bytes += bytes_read.value

                    # 搜索模式
                    data = buffer.raw[:bytes_read.value]
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
                        print(f"[+] 匹配 #{len(results)}: 0x{match_addr:016X} (区域: 0x{mbi.BaseAddress:016X}, 保护: 0x{mbi.Protect:02X})")

                        if len(results) >= max_results:
                            print(f"\n[!] 已达到最大结果数 {max_results}")
                            return results

                        offset = pos + 1

            # 移动到下一个内存区域
            address = mbi.BaseAddress + mbi.RegionSize

        print(f"\n[*] 扫描完成:")
        print(f"    - 扫描区域: {scanned_regions}")
        print(f"    - 扫描字节: {total_bytes / 1024 / 1024:.2f} MB")
        print(f"    - 找到匹配: {len(results)}")

    finally:
        CloseHandle(h_process)

    return results


def display_results(results, pattern):
    """显示搜索结果"""
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
            # 查找双字节 null 终止符
            null_pos = context.find(b'\x00\x00')
            if null_pos > 0 and null_pos % 2 == 0:
                text = context[:null_pos].decode('utf-16le', errors='ignore')
            else:
                text = context.decode('utf-16le', errors='ignore')

            # 截断显示
            if len(text) > 150:
                text = text[:150] + "..."

            print(f"  内容: {text}")
        except:
            print(f"  内容: [解码失败]")

        print()


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("用法: python memory_scanner.py <PID> <搜索文本>")
        print("示例: python memory_scanner.py 12345 你爱我还是他")
        sys.exit(1)

    try:
        pid = int(sys.argv[1])
        search_text = sys.argv[2]
    except ValueError:
        print("[-] PID 必须是数字")
        sys.exit(1)

    # 转换为 UTF-16LE
    pattern = search_text.encode('utf-16le')

    # 扫描
    results = scan_process_memory(pid, pattern)

    # 显示结果
    display_results(results, pattern)
