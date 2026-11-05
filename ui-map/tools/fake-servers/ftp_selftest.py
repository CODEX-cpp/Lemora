import ftplib, io
f = ftplib.FTP(); f.connect("127.0.0.1", 2121); f.login("prova", "x")
print(f.pwd(), f.nlst(), f.size("grande.bin"), f.voidcmd("MDTM leggimi.txt"))
f.cwd("documenti"); print(list(f.mlsd())); f.cwd("..")
f.mkd("nuova"); f.storbinary("STOR nuova/a.txt", io.BytesIO(b"ciao")); f.rename("nuova/a.txt", "nuova/b.txt")
b = io.BytesIO(); f.retrbinary("RETR nuova/b.txt", b.write); assert b.getvalue() == b"ciao"
for t in ("../../x", "/../..", "nuova/../../.."):
    try: f.cwd(t); print("CWD", t, "->", f.pwd())
    except Exception as e: print(t, e)
for t in ("../../../README.md", "/../../Windows/win.ini"):
    try: f.retrbinary("RETR " + t, b.write); print("FALLITO: letto fuori")
    except Exception as e: print("fuori respinto:", e)
f.delete("nuova/b.txt"); f.rmd("nuova"); f.quit(); print("OK")
