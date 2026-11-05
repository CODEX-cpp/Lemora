"""Server VNC finti SOLO su 127.0.0.1 per riprodurre i blocchi del client integrato:
  5902 = autenticazione VNC (tipo 2), rifiuta ogni password
  5903 = solo tipi proprietari (13, 30) come alcuni RealVNC
  5904 = tace: accetta la connessione e non manda mai il banner"""
import socket, struct, threading, os, time

def rd(c, n):
    b = b''
    while len(b) < n:
        d = c.recv(n - len(b))
        if not d: raise EOFError
        b += d
    return b

def srv_auth(c):
    try:
        c.sendall(b"RFB 003.008\n"); rd(c, 12)
        c.sendall(bytes([1, 2])); rd(c, 1)
        c.sendall(os.urandom(16)); rd(c, 16)
        c.sendall(struct.pack(">I", 1) + struct.pack(">I", 6) + b"Failed")
        time.sleep(1)
    except Exception: pass
    c.close()

def srv_prop(c):
    try:
        c.sendall(b"RFB 003.008\n"); rd(c, 12)
        c.sendall(bytes([2, 13, 30])); time.sleep(5)
    except Exception: pass
    c.close()

def srv_silent(c):
    time.sleep(60); c.close()

def listen(port, fn):
    s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", port)); s.listen(5)
    while True:
        c, _ = s.accept(); threading.Thread(target=fn, args=(c,), daemon=True).start()

for p, f in ((5902, srv_auth), (5903, srv_prop), (5904, srv_silent)):
    threading.Thread(target=listen, args=(p, f), daemon=True).start()
print("variants su 5902/5903/5904", flush=True)
open(os.path.join(os.path.dirname(__file__), "vnc_variants.pid"), "w").write(str(os.getpid()))
while True: time.sleep(3600)
