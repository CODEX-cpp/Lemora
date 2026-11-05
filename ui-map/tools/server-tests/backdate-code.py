import sqlite3, sys

# prova di scadenza: porta nel passato la scadenza del codice di abbinamento
c = sqlite3.connect(sys.argv[1])
c.execute("UPDATE pairing SET expires='2000-01-01T00:00:00.0000000Z'")
c.commit()
