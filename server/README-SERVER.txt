LEMORA SERVER (Teams) - quick start
===================================

A single file, nothing to install first (.NET is included).

WINDOWS
  1. Copy Lemora.Server.exe to the PC/server that stays on.
  2. Run it from a terminal (PowerShell):  .\Lemora.Server.exe
  3. Accept the administrator prompt: it installs itself as a service and starts on its own.
  4. The terminal shows the console address, the certificate fingerprint and the setup code.
  5. Open the address in a browser (the certificate warning is expected: it is self-signed), enter the code and create the administrator.

LINUX (with systemd)
  1. Copy Lemora.Server to the server:  chmod +x Lemora.Server
  2. Run it as root:  sudo ./Lemora.Server
  3. Same steps 4 and 5 as on Windows. The firewall (ufw/firewalld) is opened automatically.

AUTOMATIC UPDATES
  Every night at 02:00 the server checks GitHub for a new signed release and installs it.
  If the new version does not start, the previous one is put back. It can be turned off in the console.

TRY WITHOUT INSTALLING
  Lemora.Server --foreground      runs in the terminal, stop it with Ctrl+C

UNINSTALL
  Lemora.Server --uninstall           removes the service, keeps the data
  Lemora.Server --uninstall --purge   removes the data too

BACKUP
  Copy the whole data folder and the server.key file (without the key the shared connections cannot be read).

Options: --port N  --data folder
