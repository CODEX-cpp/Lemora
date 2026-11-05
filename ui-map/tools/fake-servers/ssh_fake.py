"""Server SSH finto per provare il terminale integrato (solo locale, solo prova).

Uso:  python ssh_fake.py [porta] [cartella-chiave]      (default 2222, cartella corrente)
Accesso: utente "test", password "test". Ascolta solo su 127.0.0.1.
Simula una shell con i marcatori di integrazione (OSC 133 A/C/D) e pochi comandi:
  seq N, ls, pwd, echo ..., false, uname, sleep N, slow N, clear, exit
Richiede:  pip install asyncssh
"""
import asyncio, os, sys, time
import asyncssh

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 2222
KEYDIR = sys.argv[2] if len(sys.argv) > 2 else '.'
USER, PWD = 'test', 'test'
CWD = '/home/test'


class Srv(asyncssh.SSHServer):
    def begin_auth(self, username): return True
    def password_auth_supported(self): return True
    def validate_password(self, username, password): return username == USER and password == PWD


async def shell(proc: asyncssh.SSHServerProcess):
    w = proc.stdout.write
    cols, rows = proc.get_terminal_size()[:2] if proc.get_terminal_type() else (80, 24)
    w(f'Server finto Lemora - utente {USER}\n')
    last = 0
    while True:
        w(f'\n\x1b]7;file://fake{CWD}\x07\x1b]133;A\x07\x1b[32m{USER}@fake\x1b[0m:\x1b[34m{CWD}\x1b[0m$ \x1b]133;B\x07')
        interrupted = False
        while True:
            try:
                line = await proc.stdin.readline()
                break
            except asyncssh.TerminalSizeChanged:   # il client ha ridimensionato il terminale: non e' una fine sessione
                continue
            except (asyncssh.BreakReceived, asyncssh.SignalReceived):   # Ctrl+C: nuova riga di prompt, come una shell vera
                w('^C\n'); interrupted = True; break
        if interrupted:
            continue
        if not line:
            return
        line = line.strip()
        w('\x1b]133;C\x07')
        t0 = time.time(); code = 0
        a = line.split()
        cmd = a[0] if a else ''
        try:
            if cmd == 'seq':
                for i in range(1, int(a[1]) + 1): w(f'{i}\n')
            elif cmd == 'ls': w('Desktop  Documents  Downloads  \x1b[34mprogetti\x1b[0m  note.txt\n')
            elif cmd == 'pwd': w(CWD + '\n')
            elif cmd == 'echo': w(' '.join(a[1:]) + '\n')
            elif cmd == 'uname': w('Linux fake 6.1.0 x86_64 GNU/Linux\n')
            elif cmd == 'false': code = 1
            elif cmd == 'sleep': await asyncio.sleep(float(a[1]))
            elif cmd == 'slow':
                for i in range(int(a[1])): w(f'riga lenta {i + 1}\n'); await asyncio.sleep(0.2)
            elif cmd == 'clear': w('\x1b[H\x1b[2J')
            elif cmd == 'exit': proc.exit(0); return
            elif cmd: w(f'{cmd}: comando non trovato\n'); code = 127
        except Exception as e:
            w(f'errore: {e}\n'); code = 2
        w(f'\x1b]133;D;{code}\x07')


async def main():
    kf = os.path.join(KEYDIR, 'ssh_fake_host_key')
    if not os.path.exists(kf):
        asyncssh.generate_private_key('ssh-ed25519').write_private_key(kf)
    await asyncssh.create_server(Srv, '127.0.0.1', PORT, server_host_keys=[kf], process_factory=shell, encoding='utf-8')
    print(f'SSH finto su 127.0.0.1:{PORT} utente={USER} password={PWD}', flush=True)
    await asyncio.Event().wait()

asyncio.run(main())
