"""Server VNC finto che simula un PC con PIU' MONITOR, come farebbe TightVNC: un'unica immagine con tutti i monitor uniti.
Solo 127.0.0.1:5902 (RFB 3.8, nessuna password), solo libreria standard. Serve a provare come Lemora mostra desktop molto larghi.

Uso:  python vnc_multi.py [layout] [ritardo_primo_fotogramma_s]
  row3   tre monitor 1920x1080 in fila                      (5760x1080)
  mixed  ultra-wide 2560x1080 + monitor 1920x1080           (4480x1080)
  wall   tre monitor 1920x1080 sotto e un televisore sopra  (5760x2160)
Ogni monitor ha colore, numero e misure ben visibili; nessun contenuto reale.
"""
import os, socket, struct, sys, threading, time

LAYOUTS = {
    "row3":  [(0, 0, 1920, 1080), (1920, 0, 1920, 1080), (3840, 0, 1920, 1080)],
    "mixed": [(0, 0, 2560, 1080), (2560, 0, 1920, 1080)],
    "wall":  [(0, 1080, 1920, 1080), (1920, 1080, 1920, 1080), (3840, 1080, 1920, 1080), (1920, 0, 1920, 1080)],
}
COLORS = [(40, 90, 170), (30, 130, 90), (160, 80, 40), (110, 60, 150)]
LAYOUT = sys.argv[1] if len(sys.argv) > 1 else "row3"
DELAY = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0   # secondi di attesa prima del primo fotogramma (per provare la schermata "connessione in corso")
MON = LAYOUTS[LAYOUT]
W = max(x + w for x, y, w, h in MON); H = max(y + h for x, y, w, h in MON)
NAME = ("multi-monitor-" + LAYOUT).encode()

DIG = {  # cifre 5x7
 '0': ".###. #...# #..## #.#.# ##..# #...# .###.", '1': "..#.. .##.. ..#.. ..#.. ..#.. ..#.. .###.",
 '2': ".###. #...# ....# ...#. ..#.. .#... #####", '3': "####. ....# ....# .###. ....# ....# ####.",
 '4': "...#. ..##. .#.#. #..#. ##### ...#. ...#.", '5': "##### #.... ####. ....# ....# #...# .###.",
 '6': ".###. #.... #.... ####. #...# #...# .###.", '7': "##### ....# ...#. ..#.. .#... .#... .#...",
 '8': ".###. #...# #...# .###. #...# #...# .###.", '9': ".###. #...# #...# .####  ....# ....# .###.",
 'x': ". . #...# .#.#. ..#.. .#.#. #...#", ' ': ". . . . . . .",
 'T': "##### ..#.. ..#.. ..#.. ..#.. ..#.. ..#..", 'V': "#...# #...# #...# #...# #...# .#.#. ..#..",
}

def build():
    """immagine BGRX a 32 bit (come il formato predefinito dei client: R<<16, G<<8, B)"""
    rows = [bytearray(bytes((20, 20, 20, 0)) * W) for _ in range(H)]   # sfondo scuro: spazio fuori dai monitor
    def px(c): return bytes((c[2], c[1], c[0], 0))
    def fill(x0, y0, w, h, c):
        seg = px(c) * w
        for yy in range(y0, y0 + h): rows[yy][x0 * 4:(x0 + w) * 4] = seg
    def text(s, x, y, scale, c):
        for ch in s:
            for r, row in enumerate(DIG[ch].split(' ')):
                for k, v in enumerate(row):
                    if v == '#': fill(x + k * scale, y + r * scale, scale, scale, c)
            x += 6 * scale
    for n, (x, y, w, h) in enumerate(MON):
        col = COLORS[n % len(COLORS)]
        fill(x, y, w, h, col)
        fill(x + 12, y + 12, w - 24, 6, (255, 255, 255)); fill(x + 12, y + h - 18, w - 24, 6, (255, 255, 255))   # cornice
        fill(x + 12, y + 12, 6, h - 24, (255, 255, 255)); fill(x + w - 18, y + 12, 6, h - 24, (255, 255, 255))
        label = "TV" if (LAYOUT == "wall" and n == 3) else str(n + 1)
        text(label, x + w // 2 - 3 * 30 * len(label) + 15, y + h // 2 - 105, 30, (255, 255, 255))
        text(f"{w}x{h}", x + w // 2 - 3 * 8 * len(f'{w}x{h}'), y + h // 2 + 130, 8, (235, 235, 235))
    return b"".join(bytes(r) for r in rows)

IMG = build()

def rd(c, n):
    b = b''
    while len(b) < n:
        d = c.recv(n - len(b))
        if not d: raise EOFError
        b += d
    return b

def log(m):
    # il log non deve mai far cadere la connessione (senza console o senza file di log print() puo' fallire)
    try: print(m, flush=True)
    except Exception: pass

LAST_CLOSE = 0.0
FLAKY = len(sys.argv) > 4 and sys.argv[4] == "flaky"   # come certi TightVNC: una connessione arrivata subito dopo la chiusura della precedente viene chiusa a meta' handshake

def serve(c):
    global LAST_CLOSE
    established = False
    log(f"{time.strftime('%H:%M:%S')} CONNECT")
    if FLAKY and time.time() - LAST_CLOSE < 1.5:
        try:
            c.sendall(b"RFB 003.008\n"); rd(c, 12)
        except Exception: pass
        log(f"{time.strftime('%H:%M:%S')} RIFIUTATA (troppo presto dopo la chiusura precedente)")
        c.close(); return
    try:
        c.sendall(b"RFB 003.008\n"); rd(c, 12)
        c.sendall(bytes([1, 1])); rd(c, 1)
        c.sendall(struct.pack(">I", 0)); rd(c, 1)
        fmt = (32, 24, 0, 1, 255, 255, 255, 16, 8, 0)
        c.sendall(struct.pack(">HH", W, H) + struct.pack(">BBBBHHHBBB3x", *fmt) + struct.pack(">I", len(NAME)) + NAME)
        established = True
        pending, first, last = False, True, 0
        c.settimeout(0.2)
        t0 = time.time(); desynced = False; cutsent = False
        while True:
            # prova del flusso sfasato (VNC_DESYNC=1): dopo 8 s il "server" manda un messaggio di appunti (tipo 3) con una lunghezza assurda, come il 29/10 sul PC del cliente
            if os.environ.get("VNC_DESYNC") == "1" and not desynced and time.time() - t0 > 8:
                c.settimeout(None); c.sendall(bytes([3, 0, 0, 0]) + struct.pack(">I", 0x60000000)); c.settimeout(0.2); desynced = True
                log(f"{time.strftime('%H:%M:%S')} MANDATO messaggio con lunghezza assurda")
            if os.environ.get("VNC_CUT") == "1" and not cutsent and time.time() - t0 > 6:
                msg = "dal server àè".encode("latin-1")
                c.settimeout(None); c.sendall(bytes([3, 0, 0, 0]) + struct.pack(">I", len(msg)) + msg); c.settimeout(0.2); cutsent = True
                log("MANDATO testo degli appunti del server")
            try:
                t = rd(c, 1)[0]; c.settimeout(None)
                if t == 0: d = rd(c, 19); fmt = struct.unpack(">BBBBHHHBBB", d[3:16])
                elif t == 2: n = struct.unpack(">xH", rd(c, 3))[0]; rd(c, 4 * n)
                elif t == 3: rd(c, 9); pending = True
                elif t == 4:
                    d = rd(c, 7)
                    log(f"KEY {'GIU' if d[0] else 'SU'} keysym={struct.unpack('>I', d[3:7])[0]:x}")
                elif t == 5:
                    d = rd(c, 5); log(f"PTR mask={d[0]}")
                elif t == 6:
                    rd(c, 3); txt = rd(c, struct.unpack(">I", rd(c, 4))[0]); log("CUT " + txt.decode("latin-1"))
                else: return
                c.settimeout(0.2)
            except socket.timeout:
                c.settimeout(0.2)
            if pending and not first and os.environ.get("VNC_IDLE") == "1":
                pending = False   # schermo remoto fermo (VNC_IDLE=1): dopo il primo fotogramma non c'e' nulla da mandare, come un TightVNC con il desktop immobile
            if pending and (first or time.time() - last >= 1):
                if first:
                    first = False
                    time.sleep(DELAY)
                    c.sendall(struct.pack(">BxHHHHHi", 0, 1, 0, 0, W, H, 0) + IMG)       # tutta l'immagine, una volta
                else:
                    c.sendall(struct.pack(">BxHHHHHi", 0, 1, 0, 0, 1, 1, 0) + IMG[0:4])   # solo un segno di vita
                pending, last = False, time.time()
    except EOFError:
        log(f"{time.strftime('%H:%M:%S')} DISCONNECT chiusura normale (FIN)")
    except OSError as e:
        log(f"{time.strftime('%H:%M:%S')} DISCONNECT errore di rete: {e}")
    finally:
        c.close()
        if established: LAST_CLOSE = time.time()

if __name__ == "__main__":
    HOST = sys.argv[3] if len(sys.argv) > 3 else "127.0.0.1"   # "::1" = solo IPv6 locale (li' la 5900 e' libera)
    s = socket.socket(socket.AF_INET6 if ":" in HOST else socket.AF_INET); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind((HOST, 5902)); s.listen(5)
    log(f"server VNC finto '{LAYOUT}' {W}x{H} su {HOST}:5902")
    while True:
        threading.Thread(target=serve, args=(s.accept()[0],), daemon=True).start()
