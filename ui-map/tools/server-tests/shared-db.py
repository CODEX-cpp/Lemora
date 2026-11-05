"""Aiuto per test-shared.ps1: esegue una query sul database di prova e stampa il primo valore (vuoto se nessuna riga).
Uso: shared-db.py <db> "<sql con ?>" [parametri...]
"""
import sqlite3
import sys

c = sqlite3.connect(sys.argv[1])
r = c.execute(sys.argv[2], sys.argv[3:]).fetchone()
print('' if r is None or r[0] is None else r[0])
c.close()
