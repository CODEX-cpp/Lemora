"""Aiuto per test-connections.ps1: legge/modifica il database di prova del server.
Uso:
  conn-db.py enc    <db> <nome>          stampa pw_enc della voce (vuoto se senza password)
  conn-db.py swap   <db> <nomeA> <nomeB> scambia i pw_enc di due voci (a server fermo)
  conn-db.py events <db> <kind> <chi>    conta le righe di events con quel kind e pc_id o detail uguale a <chi> ("*" = qualsiasi)
  conn-db.py seen   <db> <pcId> <iso>    forza last_seen di un PC (per provare le sessioni "interrotte")
"""
import sqlite3
import sys

cmd, dbf = sys.argv[1], sys.argv[2]
c = sqlite3.connect(dbf)
if cmd == 'enc':
    r = c.execute("SELECT pw_enc FROM connections WHERE name = ? AND deleted_at IS NULL", (sys.argv[3],)).fetchone()
    print(r[0] if r and r[0] else '')
elif cmd == 'swap':
    a = c.execute("SELECT pw_enc FROM connections WHERE name = ?", (sys.argv[3],)).fetchone()[0]
    b = c.execute("SELECT pw_enc FROM connections WHERE name = ?", (sys.argv[4],)).fetchone()[0]
    c.execute("UPDATE connections SET pw_enc = ? WHERE name = ?", (b, sys.argv[3]))
    c.execute("UPDATE connections SET pw_enc = ? WHERE name = ?", (a, sys.argv[4]))
    c.commit()
    print('ok')
elif cmd == 'events':
    kind, who = sys.argv[3], sys.argv[4]
    if who == '*':
        n = c.execute("SELECT COUNT(*) FROM events WHERE kind = ?", (kind,)).fetchone()[0]
    else:
        n = c.execute("SELECT COUNT(*) FROM events WHERE kind = ? AND (pc_id = ? OR detail = ?)", (kind, who, who)).fetchone()[0]
    print(n)
elif cmd == 'seen':
    c.execute("UPDATE pcs SET last_seen = ? WHERE id = ?", (sys.argv[4], sys.argv[3]))
    c.commit()
    print('ok')
c.close()
