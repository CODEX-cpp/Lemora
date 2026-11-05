# Notice

Lemora is free software, released under the [GNU General Public License v3.0](LICENSE).

## Origin and attribution

Lemora is a **modified version (a fork) of [1Remote](https://github.com/1Remote/1Remote)** — "One Remote Access Manager to Rule Them All" — by VShawn and the 1Remote contributors, which is also released under GPL-3.0. Lemora is **not affiliated with, endorsed by or supported by the 1Remote project**. The original name and logo are not used.

> Copyright © VShawn and the 1Remote contributors, for the code derived from 1Remote.
> Copyright © 2026 CODEX-cpp, for the changes and additions made in Lemora.

This notice, together with the license text in [LICENSE](LICENSE), is the statement of modification required by section 5(a) of the GPL-3.0.

## What was changed

Lemora was derived from the 1Remote 1.3 source tree. The main changes and additions, as of **5 November 2026**, are:

- renamed to **Lemora**, with a new logo, a new interface layout ("islands") and light/dark themes
- an **encrypted data vault** (AES-GCM) unlocked with a master password or Windows Hello
- folders with a **domain, an IP prefix and shared credentials** inherited by the connections inside them
- a **folder password manager** shown as a side tab during RDP and VNC sessions (devices, users, passwords, clipboard cleared after 30 seconds)
- **Lemora Server** (`Server/`): ASP.NET Core service and web console for team mode, with PC approval, permissions and an activity log, and the matching client code (`Ui/Setup`)
- a reworked **VNC viewer** (Tight encoding, clipboard, file transfer) and an integrated terminal based on xterm.js
- an installer with Portable / Local / Teams modes, a small **Rust launcher** and a **signed automatic update** system that uses GitHub releases (`Installer/`, `Ui/Setup/Update*.cs`)
- `.mora` connection files, variable macros, and many fixes and translations

Every file keeps its history of changes in the project's version control; where a file was changed from the 1Remote original, the change is Lemora's.

## Source code

The complete corresponding source code of every Lemora release is in this repository (each release on GitHub also offers it as a source archive): <https://github.com/CODEX-cpp/Lemora>.

## Third-party components

These components keep their own licenses, which apply to them and not to Lemora as a whole.

| Component | Where | License |
| --- | --- | --- |
| [Dragablz](https://github.com/ButchersBoy/Dragablz) (fork by the NETworkManager project) | `Dragablz/` | MIT, see [Dragablz/LICENSE](Dragablz/LICENSE) |
| VncSharpCore (customised) | `VncSharpCore/` | see [VncSharpCore/COPYING.txt](VncSharpCore/COPYING.txt) |
| [xterm.js](https://xtermjs.org/) | `Ui/Resources/Terminal/` | MIT, see [LICENSE-xterm.txt](Ui/Resources/Terminal/LICENSE-xterm.txt) |
| [PuTTY](https://www.chiark.greenend.org.uk/~sgtatham/putty/) and [KiTTY](https://github.com/cyd01/KiTTY) (portable executables, started as separate programs) | `Ui/Resources/PuTTY/`, `Ui/Resources/KiTTY/` | the licenses of their authors |
| MSTSCLib / AxMSTSCLib (Microsoft RDP ActiveX interop) | `lib/` | Microsoft |
| Shawn.Utils, VariableKeywordMatcher | `Shawn.Utils/` and a NuGet package | VShawn, same family as 1Remote |
| NuGet packages: AvalonEdit, Dapper, FluentFTP, JsonKnownTypes, Markdig.Wpf, Microsoft.Web.WebView2, MySql.Data, Newtonsoft.Json, Npgsql, NUlid, Sentry, SSH.NET, Stylet, System.Data.SQLite, System.IO.Ports, VirtualizingWrapPanel; for the server Microsoft.Data.Sqlite, SQLitePCLRaw and the Microsoft.Extensions packages | restored at build time | the license that each author publishes on [nuget.org](https://www.nuget.org/) |

If you find a missing or wrong attribution, please open an issue.
