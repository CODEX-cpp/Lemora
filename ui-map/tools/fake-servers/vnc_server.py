"""Server VNC finto (RFB 3.8, sicurezza None, Raw 800x600) SOLO su 127.0.0.1:5901, per mappare la UI di Lemora."""
import socket, struct, threading, time

W, H, NAME = 800, 600, b"fake-vnc-Lemora"
FONT = {  # 5x7
 '1': "..#.. .##.. ..#.. ..#.. ..#.. ..#.. .###.", 'R': "####. #...# #...# ####. #.#.. #..#. #...#",
 'E': "##### #.... #.... ####. #.... #.... #####", 'M': "#...# ##.## #.#.# #.#.# #...# #...# #...#",
 'O': ".###. #...# #...# #...# #...# #...# .###.", 'T': "##### ..#.. ..#.. ..#.. ..#.. ..#.. ..#..",
 'F': "##### #.... #.... ####. #.... #.... #....", 'A': ".###. #...# #...# ##### #...# #...# #...#",
 'K': "#...# #..#. #.#.. ##... #.#.. #..#. #...#", 'V': "#...# #...# #...# #...# #...# .#.#. ..#..",
 'N': "#...# ##..# #.#.# #..## #...# #...# #...#", 'C': ".#### #.... #.... #.... #.... #.... .####",
 ' ': ". . . . . . .",
}

def build():
    rgb = bytearray(W * H * 3)
    for y in range(H):
        for x in range(W):
            i = (y * W + x) * 3
            rgb[i:i+3] = bytes((x * 255 // W, y * 255 // H, 128))
    def rect(x0, y0, w, h, c):
        for y in range(y0, y0 + h):
            for x in range(x0, x0 + w):
                i = (y * W + x) * 3; rgb[i:i+3] = bytes(c)
    rect(0, 0, W, 90, (20, 20, 60))
    s, x0 = 6, 20
    for ch in "LEMORA FAKE VNC":
        for r, row in enumerate(FONT[ch].split(' ')):
            for c, v in enumerate(row):
                if v == '#': rect(x0 + c * s, 25 + r * s, s, s, (255, 255, 255))
        x0 += 6 * s
    for k, c in enumerate([(220, 30, 30), (30, 200, 30), (30, 60, 230), (240, 220, 20)]):
        rect(60 + k * 170, 200, 140, 140, c)
    rect(0, 560, 40, 40, (255, 0, 255)); rect(760, 560, 40, 40, (0, 255, 255))  # angoli di orientamento
    return rgb

IMG = build()

def encode(fmt, x, y, w, h, rgb):
    bpp, _, big, tc, rm, gm, bm, rs, gs, bs = fmt
    nb = bpp // 8; out = bytearray()
    for yy in range(y, y + h):
        for xx in range(x, x + w):
            i = (yy * W + xx) * 3
            r, g, b = rgb[i], rgb[i+1], rgb[i+2]
            v = ((r * rm // 255) << rs) | ((g * gm // 255) << gs) | ((b * bm // 255) << bs)
            out += v.to_bytes(nb, 'big' if big else 'little')
    return bytes(out)

def rd(c, n):
    b = b''
    while len(b) < n:
        d = c.recv(n - len(b))
        if not d: raise EOFError
        b += d
    return b

def serve(c):
    fmt = (32, 24, 0, 1, 255, 255, 255, 16, 8, 0)
    try:
        c.sendall(b"RFB 003.008\n"); rd(c, 12)
        c.sendall(bytes([1, 1])); rd(c, 1)          # un solo tipo: None
        c.sendall(struct.pack(">I", 0)); rd(c, 1)   # SecurityResult OK, ClientInit
        c.sendall(struct.pack(">HH", W, H) + struct.pack(">BBBBHHHBBB3x", *fmt) + struct.pack(">I", len(NAME)) + NAME)
        pending, first, last, tick = False, True, 0, 0
        c.settimeout(0.2)
        while True:
            try:
                t = rd(c, 1)[0]; c.settimeout(None)
                if t == 0: d = rd(c, 19); fmt = struct.unpack(">BBBBHHHBBB", d[3:16])
                elif t == 2: n = struct.unpack(">xH", rd(c, 3))[0]; rd(c, 4 * n)
                elif t == 3: rd(c, 9); pending = True
                elif t == 4: rd(c, 7)
                elif t == 5: rd(c, 5)
                elif t == 6: rd(c, 3); rd(c, struct.unpack(">I", rd(c, 4))[0])
                else: return
                c.settimeout(0.2)
            except socket.timeout:
                c.settimeout(0.2)
            if pending and (first or time.time() - last >= 1):
                if first: x, y, w, h, first = 0, 0, W, H, False
                else:  # piccola area (barra in basso al centro) che cambia colore ogni secondo
                    tick += 1; x, y, w, h = 300, 540, 200, 40
                    for yy in range(y, y + h):
                        for xx in range(x, x + w):
                            i = (yy * W + xx) * 3; IMG[i:i+3] = bytes(((tick * 40) % 256, 255 - (tick * 40) % 256, 255))
                c.sendall(struct.pack(">BxHHHHHi", 0, 1, x, y, w, h, 0) + encode(fmt, x, y, w, h, IMG))
                pending, last = False, time.time()
    except (EOFError, OSError):
        pass
    finally:
        c.close()

if __name__ == "__main__":
    s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", 5901)); s.listen(5)
    while True:
        threading.Thread(target=serve, args=(s.accept()[0],), daemon=True).start()
