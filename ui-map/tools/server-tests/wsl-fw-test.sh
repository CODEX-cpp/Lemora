#!/bin/bash
# Prova del firewall Linux. Uso: wsl-fw-test.sh none | ufw-off | ufw | fwd | both
# Il traffico di prova arriva da due reti finte (netns): 192.168.77.2 (privata) e 203.0.113.2 (pubblica).
MODE=$1
SRC=/mnt/c/_dev/_projects/_repos/1Remote/ui-map/_run/srv-linux
BIN=/opt/lemora-server/Lemora.Server
DATA=/var/lib/lemora-server
NS_OK=0

prep_net() {
  ip netns del cl 2>/dev/null; ip netns del pub 2>/dev/null; ip link del vh 2>/dev/null; ip link del vh2 2>/dev/null
  ip netns add cl; ip link add vh type veth peer name vc; ip link set vc netns cl
  ip addr add 192.168.77.1/24 dev vh; ip link set vh up
  ip netns exec cl ip addr add 192.168.77.2/24 dev vc; ip netns exec cl ip link set vc up; ip netns exec cl ip link set lo up
  ip netns add pub; ip link add vh2 type veth peer name vc2; ip link set vc2 netns pub
  ip addr add 203.0.113.1/24 dev vh2; ip link set vh2 up
  ip netns exec pub ip addr add 203.0.113.2/24 dev vc2; ip netns exec pub ip link set vc2 up; ip netns exec pub ip link set lo up
}
drop_net() { ip netns del cl 2>/dev/null; ip netns del pub 2>/dev/null; ip link del vh 2>/dev/null; ip link del vh2 2>/dev/null; }

# prova(rete, indirizzo_server, porta): apre un ascoltatore sull'indirizzo e prova a collegarsi dalla rete indicata
prova() {
  local ns=$1 ip=$2 port=$3
  nc -l -k $ip $port >/dev/null 2>&1 &
  local pid=$!
  sleep 0.5
  if ip netns exec $ns nc -z -w 3 $ip $port 2>/dev/null; then echo ALLOWED; else echo BLOCKED; fi
  kill $pid 2>/dev/null; wait $pid 2>/dev/null
}
traffico() { # porta_aperta porta_chiusa
  echo "   rete privata  -> porta $1 : $(prova cl 192.168.77.1 $1)   (atteso ALLOWED)"
  echo "   rete pubblica -> porta $1 : $(prova pub 203.0.113.1 $1)   (atteso BLOCKED)"
  echo "   rete privata  -> porta $2 : $(prova cl 192.168.77.1 $2)   (atteso BLOCKED)"
}
porta() { sed -n 's/.*"port":\([0-9]*\).*/\1/p' $DATA/runtime.json; }
stato() {
  echo "-- ufw:"; ufw status 2>&1 | head -12
  echo "-- firewalld:"; (systemctl is-active firewalld; firewall-cmd --list-rich-rules; firewall-cmd --permanent --list-rich-rules | sed 's/^/(permanente) /'; ls /etc/firewalld/services/ 2>&1) 2>&1 | head -14
  echo "-- file:"; ls /etc/ufw/applications.d/lemora-server /etc/firewalld/services/lemora-server.xml 2>&1
}

echo "######## modo: $MODE"
rm -rf /opt/lemora-server; mkdir -p /opt/lemora-server; cp -r $SRC/. /opt/lemora-server/; chmod +x $BIN
$BIN --uninstall --purge >/dev/null 2>&1
ufw --force reset >/dev/null 2>&1; ufw --force disable >/dev/null 2>&1
systemctl stop firewalld 2>/dev/null
case $MODE in
  ufw-off) ;;                                              # ufw installato ma spento
  ufw)  ufw default deny incoming >/dev/null; ufw --force enable >/dev/null ;;
  fwd)  systemctl start firewalld; sleep 3 ;;
  both) ufw default deny incoming >/dev/null; ufw --force enable >/dev/null; systemctl start firewalld; sleep 3 ;;
esac
echo "== stato prima dell'installazione"; stato
prep_net

echo "== INSTALLA"; $BIN | grep -E "attivo|Impronta|ATTENZIONE|Nessun|non ha" ; sleep 3
P1=$(porta); echo "porta usata: $P1"
echo "== stato dopo l'installazione"; stato
echo "== traffico"; traffico $P1 $((P1+1))

echo "== CAMBIO PORTA (come dal wizard): server.json con porta 9443"
systemctl stop lemora-server
echo '{"DbDir":"/var/lib/lemora-server/db","Port":9443,"Listen":"all","Configured":true}' > $DATA/server.json; chmod 644 $DATA/server.json
systemctl start lemora-server; sleep 6
P2=$(porta); echo "porta usata: $P2"
echo "== stato dopo il cambio porta"; stato
echo "== traffico (nuova porta; la vecchia deve essere chiusa)"; traffico $P2 $P1

echo "== RIMOZIONE"; $BIN --uninstall --purge
echo "== stato dopo la rimozione"; stato
ufw status | grep -ci "Lemora" | sed 's/^/righe Lemora residue in ufw: /'
firewall-cmd --permanent --list-rich-rules 2>/dev/null | grep -c lemora | sed 's/^/regole lemora residue in firewalld: /'
drop_net
ufw --force disable >/dev/null 2>&1; systemctl stop firewalld 2>/dev/null
rm -rf /opt/lemora-server
echo "######## fine $MODE"
