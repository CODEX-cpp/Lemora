# Lemora

Lemora è un gestore di connessioni remote per Windows (WPF/.NET): raccoglie in un solo programma le connessioni RDP, SSH, VNC, Telnet, SFTP/FTP e le app esterne, con cartelle, credenziali, schede staccabili e avvio rapido (Alt + M).

## Come si compila

Requisiti, sottomoduli e comandi di build sono in [DEVELOP.md](DEVELOP.md). In breve:

1. clona il repository (le librerie personalizzate, come VncSharpCore e Dragablz, sono già dentro: niente sottomoduli);
2. apri la soluzione in Visual Studio 2022 o più recente, con l'SDK .NET indicato in DEVELOP.md;
3. ripristina i pacchetti NuGet e compila.

## Licenza e attribuzione

Lemora è distribuito con licenza [GPL-3.0](LICENSE). Contiene codice © VShawn e contributori, con la stessa licenza.
