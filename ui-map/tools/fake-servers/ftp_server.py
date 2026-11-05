"""Server FTP finto per la mappatura UI. Solo 127.0.0.1:2121, solo libreria standard.
Utente anonimo oppure prova/x. Tutto confinato in ROOT. Niente TLS (AUTH rifiutato)."""
import os, socket, socketserver, time, posixpath

ROOT = os.path.realpath(r"C:\_dev\_projects\_repos\1Remote\ui-map\_run\ftp-root")
HOST, PORT = "127.0.0.1", 2121
USERS = {"anonymous": None, "ftp": None, "prova": "x"}


def seed():
    os.makedirs(ROOT, exist_ok=True)
    if os.listdir(ROOT):
        return
    for d in ("documenti", "immagini", "archivio/2024", "vuota"):
        os.makedirs(os.path.join(ROOT, d), exist_ok=True)
    files = {"leggimi.txt": 120, "documenti/relazione.txt": 5000, "documenti/dati.csv": 20000,
             "immagini/foto.bin": 300000, "archivio/2024/vecchio.log": 1500, "grande.bin": 3000000}
    for i, (n, s) in enumerate(files.items()):
        p = os.path.join(ROOT, n)
        with open(p, "wb") as f:
            f.write((b"Lemora prova %d\n" % i) * (s // 16 + 1))
            f.truncate(s)
        t = time.time() - 86400 * (7 + i * 40)
        os.utime(p, (t, t))


def mtime(st):
    return time.strftime("%Y%m%d%H%M%S", time.gmtime(st.st_mtime))


class Handler(socketserver.StreamRequestHandler):
    def reply(self, s):
        self.wfile.write((s + "\r\n").encode("utf-8"))
        self.wfile.flush()

    def real(self, p):
        """percorso virtuale -> (reale, virtuale); reale None se esce dalla radice"""
        v = posixpath.normpath(p if p.startswith("/") else posixpath.join(self.cwd, p))
        v = "/" + v.lstrip("/")
        r = os.path.realpath(os.path.join(ROOT, v.lstrip("/")))
        if r != ROOT and not r.startswith(ROOT + os.sep):
            return None, v
        return r, v

    def handle(self):
        self.cwd, self.user, self.ok = "/", None, False
        self.pasv = None
        self.rnfr = None
        self.rest = 0
        self.reply("220 Lemora fake FTP ready")
        while True:
            line = self.rfile.readline()
            if not line:
                break
            line = line.decode("utf-8", "replace").rstrip("\r\n")
            cmd, _, arg = line.partition(" ")
            try:
                if not self.dispatch(cmd.upper(), arg):
                    break
            except Exception as e:
                self.reply("550 errore: %s" % e)
        if self.pasv:
            self.pasv.close()

    def dispatch(self, c, a):
        if c == "USER":
            self.user = a
            self.reply("331 password richiesta")
        elif c == "PASS":
            u = (self.user or "").lower()
            if u in ("anonymous", "ftp") or USERS.get(self.user) == a:
                self.ok = True
                self.reply("230 accesso eseguito")
            else:
                self.reply("530 accesso negato")
        elif c == "QUIT":
            self.reply("221 ciao")
            return False
        elif c in ("AUTH", "PBSZ", "PROT"):
            self.reply("502 TLS non supportato")
        elif c == "SYST":
            self.reply("215 UNIX Type: L8")
        elif c == "FEAT":
            self.wfile.write(b"211-Features:\r\n UTF8\r\n MLST type*;size*;modify*;\r\n MDTM\r\n SIZE\r\n REST STREAM\r\n EPSV\r\n211 End\r\n")
            self.wfile.flush()
        elif c in ("OPTS", "NOOP", "ABOR"):
            self.reply("200 ok")
        elif not self.ok:
            self.reply("530 non autenticato")
        elif c in ("PWD", "XPWD"):
            self.reply('257 "%s" cartella corrente' % self.cwd)
        elif c in ("CWD", "XCWD"):
            r, v = self.real(a)
            if r and os.path.isdir(r):
                self.cwd = v
                self.reply("250 ok")
            else:
                self.reply("550 cartella non trovata")
        elif c in ("CDUP", "XCUP"):
            self.cwd = posixpath.dirname(self.cwd) or "/"
            self.reply("250 ok")
        elif c == "TYPE":
            self.reply("200 tipo %s" % a[:1].upper())
        elif c in ("PASV", "EPSV"):
            if self.pasv:
                self.pasv.close()
            self.pasv = socket.socket()
            self.pasv.bind((HOST, 0))
            self.pasv.listen(1)
            self.pasv.settimeout(15)
            p = self.pasv.getsockname()[1]
            if c == "PASV":
                self.reply("227 Entering Passive Mode (127,0,0,1,%d,%d)" % (p >> 8, p & 255))
            else:
                self.reply("229 Entering Extended Passive Mode (|||%d|)" % p)
        elif c in ("LIST", "NLST", "MLSD"):
            args = [x for x in a.split() if not x.startswith("-")]
            r, v = self.real(" ".join(args) if args else ".")
            if not r or not os.path.isdir(r):
                self.reply("550 cartella non trovata")
                return True
            out = []
            for n in sorted(os.listdir(r)):
                fp = os.path.join(r, n)
                st = os.stat(fp)
                d = os.path.isdir(fp)
                if c == "NLST":
                    out.append(n)
                elif c == "MLSD":
                    out.append("type=%s;size=%d;modify=%s; %s" % ("dir" if d else "file", st.st_size, mtime(st), n))
                else:
                    out.append("%srwxr-xr-x 1 ftp ftp %12d %s %s" % ("d" if d else "-", st.st_size, time.strftime("%b %d %Y", time.gmtime(st.st_mtime)), n))
            self.send_data(("\r\n".join(out) + ("\r\n" if out else "")).encode("utf-8"))
        elif c == "MLST":
            r, v = self.real(a or ".")
            if r and os.path.exists(r):
                st = os.stat(r)
                self.wfile.write(("250-ok\r\n type=%s;size=%d;modify=%s; %s\r\n250 End\r\n" % ("dir" if os.path.isdir(r) else "file", st.st_size, mtime(st), v)).encode())
                self.wfile.flush()
            else:
                self.reply("550 non trovato")
        elif c == "SIZE":
            r, v = self.real(a)
            if r and os.path.isfile(r):
                self.reply("213 %d" % os.path.getsize(r))
            else:
                self.reply("550 file non trovato")
        elif c == "MDTM":
            r, v = self.real(a)
            if r and os.path.exists(r):
                self.reply("213 " + mtime(os.stat(r)))
            else:
                self.reply("550 non trovato")
        elif c == "REST":
            self.rest = int(a or 0)
            self.reply("350 ok")
        elif c == "RETR":
            r, v = self.real(a)
            if not r or not os.path.isfile(r):
                self.reply("550 file non trovato")
                return True
            with open(r, "rb") as f:
                f.seek(self.rest)
                data = f.read()
            self.rest = 0
            self.send_data(data)
        elif c in ("STOR", "APPE"):
            r, v = self.real(a)
            if not r or not os.path.isdir(os.path.dirname(r)):
                self.reply("550 percorso non valido")
                return True
            s = self.open_data()
            if not s:
                return True
            with open(r, "ab" if c == "APPE" or self.rest else "wb") as f:
                while True:
                    b = s.recv(65536)
                    if not b:
                        break
                    f.write(b)
            s.close()
            self.rest = 0
            self.reply("226 ricevuto")
        elif c == "DELE":
            r, v = self.real(a)
            if r and os.path.isfile(r):
                os.remove(r)
                self.reply("250 eliminato")
            else:
                self.reply("550 file non trovato")
        elif c in ("MKD", "XMKD"):
            r, v = self.real(a)
            if r and not os.path.exists(r) and os.path.isdir(os.path.dirname(r)):
                os.mkdir(r)
                self.reply('257 "%s" creata' % v)
            else:
                self.reply("550 impossibile creare")
        elif c in ("RMD", "XRMD"):
            r, v = self.real(a)
            if r and r != ROOT and os.path.isdir(r):
                try:
                    os.rmdir(r)
                    self.reply("250 eliminata")
                except OSError:
                    self.reply("550 cartella non vuota")
            else:
                self.reply("550 cartella non trovata")
        elif c == "RNFR":
            r, v = self.real(a)
            if r and r != ROOT and os.path.exists(r):
                self.rnfr = r
                self.reply("350 pronto per RNTO")
            else:
                self.reply("550 non trovato")
        elif c == "RNTO":
            r, v = self.real(a)
            if self.rnfr and r and not os.path.exists(r):
                os.rename(self.rnfr, r)
                self.reply("250 rinominato")
            else:
                self.reply("553 impossibile rinominare")
            self.rnfr = None
        else:
            self.reply("502 comando non implementato")
        return True

    def open_data(self):
        if not self.pasv:
            self.reply("425 usare PASV")
            return None
        self.reply("150 apro connessione dati")
        try:
            s, _ = self.pasv.accept()
        except Exception:
            self.reply("425 connessione dati fallita")
            return None
        self.pasv.close()
        self.pasv = None
        return s

    def send_data(self, data):
        s = self.open_data()
        if not s:
            return
        s.sendall(data)
        s.close()
        self.reply("226 trasferimento completato")


class Srv(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    seed()
    with Srv((HOST, PORT), Handler) as s:
        s.serve_forever()
