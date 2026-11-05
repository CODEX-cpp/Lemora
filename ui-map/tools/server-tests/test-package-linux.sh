#!/bin/bash
# Avvia il pacchetto Linux in primo piano con dati temporanei e controlla che la console risponda.
src="$1"
d=$(mktemp -d)
cp "$src" "$d/srv" && chmod +x "$d/srv"
"$d/srv" --foreground --no-pause --port 47632 --data "$d/data" > "$d/log" 2>&1 &
pid=$!
ok=0
for i in $(seq 1 30); do
  if curl -sk https://127.0.0.1:47632/ | grep -qi '<html'; then ok=1; break; fi
  sleep 0.7
done
[ -f "$d/data/setup-code.txt" ] && [ -f "$d/data/runtime.json" ] || ok=0
ls "$d/data"
kill $pid 2>/dev/null
[ $ok = 1 ] && echo LINUX_OK || { echo LINUX_FAIL; tail -20 "$d/log"; }
rm -rf "$d"
