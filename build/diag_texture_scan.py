"""Find a region of this desktop with enough texture for a meaningful mosaic test."""
import ctypes
import ctypes.wintypes as wt

u = ctypes.windll.user32
g = ctypes.windll.gdi32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


class BI(ctypes.Structure):
    _fields_ = [("bmiHeader", BIH), ("bmiColors", wt.DWORD * 3)]


def grab(x, y, w, h):
    hdc = u.GetDC(0)
    mem = g.CreateCompatibleDC(hdc)
    bmp = g.CreateCompatibleBitmap(hdc, w, h)
    g.SelectObject(mem, bmp)
    g.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)
    bi = BI()
    bi.bmiHeader.biSize = ctypes.sizeof(BIH)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0
    buf = ctypes.create_string_buffer(w * h * 4)
    g.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    g.DeleteObject(bmp)
    g.DeleteDC(mem)
    u.ReleaseDC(0, hdc)
    return buf.raw


def texture(b, w, h, tol=12):
    n = w * h
    sr = sg = sb = 0
    for i in range(0, n * 4, 4):
        sr += b[i]; sg += b[i+1]; sb += b[i+2]
    mr, mg, mb = sr/n, sg/n, sb/n
    c = 0
    for i in range(0, n * 4, 4):
        if abs(b[i]-mr) > tol or abs(b[i+1]-mg) > tol or abs(b[i+2]-mb) > tol:
            c += 1
    return c / n


SW, SH = u.GetSystemMetrics(78), u.GetSystemMetrics(79)
print(f"screen {SW}x{SH}")
best = []
for y in range(100, SH - 300, 200):
    for x in range(100, SW - 400, 300):
        t = texture(grab(x, y, 340, 260), 340, 260)
        best.append((t, x, y))
best.sort(reverse=True)
print("top textured 340x260 regions:")
for t, x, y in best[:12]:
    print(f"  ({x:5d},{y:5d}) texture={t:.3f}")
