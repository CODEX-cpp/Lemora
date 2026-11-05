# Aiuto per test-admins.ps1: legge/modifica il database del server di prova.
#   rows <db>                 -> JSON degli amministratori (user, hash, created_by, main)
#   tables <db>               -> nomi delle tabelle, separati da virgola
#   events <db> <kind>        -> righe "detail" di quel tipo, separate da |
#   contains <db> <testo>     -> 1 se il testo compare nei byte del file del database
#   to-old <db>               -> riporta il database al formato vecchio (tabella admin con un solo amministratore)
import json, sqlite3, sys

cmd, db = sys.argv[1], sys.argv[2]
if cmd == 'contains':
    print(1 if sys.argv[3].encode() in open(db, 'rb').read() else 0)
    sys.exit()
c = sqlite3.connect(db)
if cmd == 'rows':
    print(json.dumps([dict(zip(['user', 'hash', 'created_by', 'main'], r)) for r in c.execute('SELECT user, hash, created_by, main FROM admins')]))
elif cmd == 'tables':
    print(','.join(r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")))
elif cmd == 'events':
    print('|'.join(r[0] or '' for r in c.execute('SELECT detail FROM events WHERE kind = ? ORDER BY id', (sys.argv[3],))))
elif cmd == 'to-old':
    user, h = c.execute('SELECT user, hash FROM admins WHERE main = 1').fetchone()
    c.executescript('DROP TABLE admins; CREATE TABLE admin (id INTEGER PRIMARY KEY CHECK (id = 1), user TEXT NOT NULL, hash TEXT NOT NULL);')
    c.execute('INSERT INTO admin (id, user, hash) VALUES (1, ?, ?)', (user, h))
    c.commit()
    print(h)
