#!/usr/bin/env python3
"""生成良子小窗的应用图标（assets/icon.png 与 assets/icon.ico）。

图标只用几何图形画出来，尺寸由代码算出 —— 仓库里不放手工维护的图片资源，
这个脚本就是唯一的来源；改设计改这里再跑一次即可。

    python build/make-icon.py

不依赖 Pillow：PNG 与 ICO 都按格式手写，抗锯齿用 4x4 超采样。
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# 放在桌面项目里：csproj 要引用 .ico 当 exe 图标，打包脚本再从同一个 PNG 生成 .icns。
ASSETS = os.path.join(ROOT, "src", "Reader.Desktop", "Assets")

# 品牌墨绿，与旧托盘图标、界面强调色一致。
BACKDROP = (0x29, 0x5B, 0x46)
LINE_LONG = (0xF1, 0xF4, 0xF0)
LINE_SHORT = (0x8F, 0xB3, 0xA4)

SAMPLES = 4  # 每个像素每轴 4 个子采样点
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
MASTER_SIZE = 512


def rounded_rect(x, y, left, top, right, bottom, radius):
    """圆角矩形内部的判定，用标准 SDF。"""
    half_w = (right - left) / 2 - radius
    half_h = (bottom - top) / 2 - radius
    cx = (left + right) / 2
    cy = (top + bottom) / 2
    dx = abs(x - cx) - half_w
    dy = abs(y - cy) - half_h
    outside = math.hypot(max(dx, 0.0), max(dy, 0.0))
    inside = min(max(dx, dy), 0.0)
    return outside + inside - radius <= 0


def render(size):
    """按目标尺寸现画（不是缩放母图），小尺寸才不会有糊边。"""
    margin = size * 0.045
    radius = size * 0.235
    # 两条横线：长的一条像正文，短的一条收尾。
    long_bar = (size * 0.235, size * 0.335, size * 0.765, size * 0.455, size * 0.060)
    short_bar = (size * 0.235, size * 0.545, size * 0.600, size * 0.645, size * 0.050)

    step = 1.0 / SAMPLES
    pixels = bytearray(size * size * 4)
    for py in range(size):
        for px in range(size):
            r = g = b = a = 0.0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    x = px + (sx + 0.5) * step
                    y = py + (sy + 0.5) * step
                    if not rounded_rect(x, y, margin, margin, size - margin, size - margin, radius):
                        continue
                    colour = BACKDROP
                    if rounded_rect(x, y, *long_bar):
                        colour = LINE_LONG
                    elif rounded_rect(x, y, *short_bar):
                        colour = LINE_SHORT
                    r += colour[0]
                    g += colour[1]
                    b += colour[2]
                    a += 255
            total = SAMPLES * SAMPLES
            index = (py * size + px) * 4
            if a > 0:
                # 颜色按覆盖到的子采样点求平均，alpha 单独取覆盖率，边缘才不会发黑。
                covered = a / 255
                pixels[index] = round(r / covered)
                pixels[index + 1] = round(g / covered)
                pixels[index + 2] = round(b / covered)
                pixels[index + 3] = round(a / total)
    return bytes(pixels)


# 手写 PNG：IHDR + 压缩后的 IDAT + IEND，CRC 覆盖块类型和载荷。
def png_bytes(size, pixels):
    def chunk(tag, payload):
        body = tag + payload
        return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + pixels[y * size * 4:(y + 1) * size * 4] for y in range(size))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def ico_bytes(images):
    """ICO 里每个尺寸都直接存 PNG（Vista 以后支持，Windows 10/11 无问题）。"""
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, payload = b"", b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        payload += data
        offset += len(data)
    return header + entries + payload


# 主图 512 供 .icns 和文档使用；ICO 每个尺寸都重新渲染，避免缩放糊边。
def main():
    os.makedirs(ASSETS, exist_ok=True)
    master = png_bytes(MASTER_SIZE, render(MASTER_SIZE))
    with open(os.path.join(ASSETS, "icon.png"), "wb") as f:
        f.write(master)
    small = [(size, png_bytes(size, render(size))) for size in ICO_SIZES]
    with open(os.path.join(ASSETS, "icon.ico"), "wb") as f:
        f.write(ico_bytes(small))
    print(f"src/Reader.Desktop/Assets/icon.png ({MASTER_SIZE}x{MASTER_SIZE}) "
          f"与 icon.ico ({len(small)} 个尺寸) 已生成")


if __name__ == "__main__":
    main()
