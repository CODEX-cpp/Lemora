// Stable entry point of Lemora (same idea as the TrackFlow launcher).
// Shortcuts, start with Windows and .mora files ALWAYS point to this Lemora.exe in the installation folder,
// never to a specific version. The real app lives in versions\<version>\Lemora.exe and current.txt says which one to use:
// an update adds a new folder and changes current.txt, without ever touching a file in use.
//
// Special option (only for updates): "--after-pid <number>" waits for that process to end before starting
// the app, so the new version does not find the old one still open. The rest of the arguments pass to the app unchanged.
//
// Deliberately without external libraries: it must start right away and have very little that can break.
#![windows_subsystem = "windows"]

use std::env;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;

const CURRENT_FILE: &str = "current.txt";
const VERSIONS_DIR: &str = "versions";
const APP_EXE: &str = "Lemora.exe";

/// Option for operations with administrator permissions (update, uninstall): "--lemora-elevated wait|nowait <arguments>".
/// The app looks for it as text inside this file to know whether the installed launcher supports it (old launchers do not): do not change it.
const ELEVATED_FLAG: &str = "--lemora-elevated";
/// marker the app looks for in the launcher's bytes (SetupEngine.ElevatedLauncher); black_box keeps it in the exe even with LTO
static ELEVATED_MARKER: &[u8] = b"LEMORA-ELEVATED-V1";

#[cfg(windows)]
mod win {
    use std::ffi::c_void;
    extern "system" {
        pub fn OpenProcess(access: u32, inherit: i32, pid: u32) -> *mut c_void;
        pub fn WaitForSingleObject(handle: *mut c_void, ms: u32) -> u32;
        pub fn CloseHandle(handle: *mut c_void) -> i32;
        pub fn CreateDirectoryW(path: *const u16, sa: *const SecurityAttributes) -> i32;
        pub fn GetWindowsDirectoryW(buf: *mut u16, size: u32) -> u32;
        pub fn GetLastError() -> u32;
        pub fn LocalFree(h: *mut c_void) -> *mut c_void;
        pub fn GetUserDefaultUILanguage() -> u16;
    }
    #[link(name = "advapi32")]
    extern "system" {
        pub fn ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl: *const u16, rev: u32, sd: *mut *mut c_void, size: *mut u32) -> i32;
    }
    #[repr(C)]
    pub struct SecurityAttributes {
        pub n_length: u32,
        pub sd: *mut c_void,
        pub inherit: i32,
    }
    pub const SYNCHRONIZE: u32 = 0x0010_0000;
    pub const ERROR_ALREADY_EXISTS: u32 = 183;
}

#[cfg(windows)]
fn wide(s: &std::ffi::OsStr) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    s.encode_wide().chain(std::iter::once(0)).collect()
}

/// Windows folder asked from the system (not from %SystemRoot%, which comes from the environment of whoever requested elevation)
#[cfg(windows)]
fn cartella_windows() -> Option<PathBuf> {
    use std::os::windows::ffi::OsStringExt;
    let mut buf = [0u16; 260];
    let n = unsafe { win::GetWindowsDirectoryW(buf.as_mut_ptr(), buf.len() as u32) } as usize;
    if n == 0 || n >= buf.len() { return None; }
    Some(PathBuf::from(std::ffi::OsString::from_wide(&buf[..n])))
}

/// Creates a NEW folder (fails if it already exists) with its own permissions: owner Administrators, only SYSTEM and Administrators,
/// no inheritance from the parent folder. Without administrator permissions Windows rejects the owner and the creation fails.
#[cfg(windows)]
fn crea_cartella_protetta(p: &Path) -> Result<(), u32> {
    let sddl = wide(std::ffi::OsStr::new("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"));
    let mut sd: *mut std::ffi::c_void = std::ptr::null_mut();
    unsafe {
        if win::ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_ptr(), 1, &mut sd, std::ptr::null_mut()) == 0 {
            return Err(win::GetLastError());
        }
        let sa = win::SecurityAttributes { n_length: std::mem::size_of::<win::SecurityAttributes>() as u32, sd, inherit: 0 };
        let ok = win::CreateDirectoryW(wide(p.as_os_str()).as_ptr(), &sa);
        let err = if ok == 0 { win::GetLastError() } else { 0 };
        win::LocalFree(sd);
        if ok == 0 { Err(err) } else { Ok(()) }
    }
}

fn copia_cartella(da: &Path, a: &Path) -> std::io::Result<()> {
    fs::create_dir_all(a)?;
    for e in fs::read_dir(da)? {
        let e = e?;
        let dest = a.join(e.file_name());
        if e.file_type()?.is_dir() { copia_cartella(&e.path(), &dest)?; } else { fs::copy(e.path(), &dest)?; }
    }
    Ok(())
}

/// Variables that make .NET load code (profiler, startup hook, extra dependencies, extraction folder): they come from the
/// unelevated user's environment (including HKCU\Environment), so they must not reach the process running with administrator permissions.
fn variabile_pericolosa(nome: &str) -> bool {
    let n = nome.to_ascii_uppercase();
    // COREHOST_*: .NET host trace to a file of choice; LEMORA_*: test settings and diagnostic breadcrumbs, they too write
    // files wherever the variable says. In the elevated process they would become administrator writes
    n.starts_with("DOTNET_") || n.starts_with("COMPLUS_") || n.starts_with("CORECLR_") || n.starts_with("COREHOST_") || n.starts_with("LEMORA_")
}

#[cfg(test)]
mod test {
    #[test]
    fn variabili_che_caricano_codice_tolte() {
        for n in ["DOTNET_STARTUP_HOOKS", "dotnet_bundle_extract_base_dir", "COMPlus_EnableDiagnostics", "CORECLR_PROFILER_PATH", "CORECLR_ENABLE_PROFILING"] {
            assert!(super::variabile_pericolosa(n), "{n}");
        }
        for n in ["COREHOST_TRACEFILE", "corehost_trace", "LEMORA_BREADCRUMB", "LEMORA_HEADLESS"] {
            assert!(super::variabile_pericolosa(n), "{n}");
        }
        for n in ["PATH", "TEMP", "MY_DOTNET_X", "MYLEMORA_X"] {
            assert!(!super::variabile_pericolosa(n), "{n}");
        }
    }

    #[cfg(windows)]
    #[test]
    fn cartella_protetta_nuova_e_mai_riusata() {
        let p = std::env::temp_dir().join(format!("lemora-launcher-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        // as an unelevated user Windows rejects the Administrators owner: the creation must fail without leaving anything behind
        match super::crea_cartella_protetta(&p) {
            Ok(()) => {
                assert_eq!(super::crea_cartella_protetta(&p), Err(super::win::ERROR_ALREADY_EXISTS));
                let _ = std::fs::remove_dir_all(&p);
            }
            Err(_) => assert!(!p.exists()),
        }
    }
}

/// Start with administrator permissions (risk avoided: native DLLs extracted into the user's %TEMP%, uninstall copy in %TEMP%).
/// The launcher lives in the installation folder (writable only by administrators) and extracts nothing. Already elevated:
/// 1. creates a new administrators-only folder in Windows\SystemTemp (or Windows\Temp);
/// 2. copies the current app version into it (so the uninstall can delete the installation folder);
/// 3. starts it with the .NET extraction folder in there and without the variables that make it load code;
/// 4. "wait": waits, deletes the folder and returns the app's exit code; "nowait": exits right away (the app cleans up by itself).
#[cfg(windows)]
fn avvio_elevato(cartella: &Path, app_dir: &Path, attesa: bool, argomenti: &[String]) -> i32 {
    let Some(windows) = cartella_windows() else { segnala_errore(cartella, &testo("cartella di Windows non trovata".into(), "Windows folder not found".into())); return 1 };
    let base = if windows.join("SystemTemp").is_dir() { windows.join("SystemTemp") } else { windows.join("Temp") };
    let t = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
    let mut lavoro = None;
    for i in 0..20u32 {
        let p = base.join(format!("Lemora-elev-{}-{:x}-{i}", std::process::id(), t));
        match crea_cartella_protetta(&p) {
            Ok(()) => { lavoro = Some(p); break; }
            Err(win::ERROR_ALREADY_EXISTS) => continue,
            Err(e) => { segnala_errore(cartella, &testo(format!("cartella protetta {} non creata (errore {e}): servono i permessi di amministratore", p.display()),
                format!("protected folder {} not created (error {e}): administrator permissions are required", p.display()))); return 1; }
        }
    }
    let Some(lavoro) = lavoro else { segnala_errore(cartella, &testo("nessun nome libero per la cartella protetta".into(), "no free name for the protected folder".into())); return 1 };
    let app_copia = lavoro.join("app");
    let pulisci = |l: &Path| { for _ in 0..20 { if fs::remove_dir_all(l).is_ok() || !l.exists() { break; } std::thread::sleep(std::time::Duration::from_millis(500)); } };
    if let Err(e) = copia_cartella(app_dir, &app_copia) {
        segnala_errore(cartella, &testo(format!("copia di {} non riuscita: {e}", app_dir.display()), format!("copy of {} failed: {e}", app_dir.display())));
        pulisci(&lavoro);
        return 1;
    }
    let mut cmd = Command::new(app_copia.join(APP_EXE));
    cmd.args(argomenti).current_dir(&app_copia).env_clear();
    cmd.envs(env::vars_os().filter(|(k, _)| !variabile_pericolosa(&k.to_string_lossy())));
    cmd.env("DOTNET_BUNDLE_EXTRACT_BASE_DIR", lavoro.join("x"));
    let mut figlio = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => { segnala_errore(cartella, &testo(format!("avvio della copia protetta non riuscito: {e}"), format!("start of the protected copy failed: {e}"))); pulisci(&lavoro); return 1; }
    };
    if !attesa { return 0; }
    let codice = figlio.wait().ok().and_then(|s| s.code()).unwrap_or(1);
    pulisci(&lavoro);
    codice
}

/// waits (at most 30 seconds) for the process with this id to end; if it no longer exists it returns right away
fn aspetta_processo(pid: u32) {
    #[cfg(windows)]
    unsafe {
        let h = win::OpenProcess(win::SYNCHRONIZE, 0, pid);
        if !h.is_null() {
            win::WaitForSingleObject(h, 30_000);
            win::CloseHandle(h);
        }
    }
    #[cfg(not(windows))]
    let _ = pid;
}

/// the launcher's messages: Italian when the Windows UI language is Italian (primary language id 0x10), English otherwise
fn testo(it: String, en: String) -> String {
    #[cfg(windows)]
    let italiano = (unsafe { win::GetUserDefaultUILanguage() } & 0x3ff) == 0x10;
    #[cfg(not(windows))]
    let italiano = false;
    if italiano { it } else { en }
}

fn segnala_errore(cartella: &Path, messaggio: &str) {
    // no console (windows_subsystem = "windows"): the error is written to a file next to the launcher
    if let Ok(mut f) = fs::OpenOptions::new().create(true).append(true).open(cartella.join("launcher-error.log")) {
        let _ = writeln!(f, "{messaggio}");
    }
}

/// the most recent version among the versions\ folders that contain the app: safety net if current.txt is missing or wrong
fn ultima_versione(cartella: &Path) -> Option<String> {
    let mut v: Vec<(Vec<u64>, String)> = fs::read_dir(cartella.join(VERSIONS_DIR))
        .ok()?
        .filter_map(Result::ok)
        .filter(|e| e.path().join(APP_EXE).exists())
        .map(|e| e.file_name().to_string_lossy().to_string())
        .map(|n| (n.split('.').map(|p| p.parse::<u64>().unwrap_or(0)).collect(), n))
        .collect();
    v.sort();
    v.pop().map(|(_, n)| n)
}

fn main() {
    let Some(cartella) = env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)) else { return };

    let mut argomenti: Vec<String> = env::args().skip(1).collect();
    if let Some(i) = argomenti.iter().position(|a| a == "--after-pid") {
        if let Some(pid) = argomenti.get(i + 1).and_then(|p| p.parse::<u32>().ok()) {
            aspetta_processo(pid);
        }
        let fine = (i + 2).min(argomenti.len());
        argomenti.drain(i..fine);
    }

    let scelta = fs::read_to_string(cartella.join(CURRENT_FILE)).map(|s| s.trim().to_string()).unwrap_or_default();
    let app_di = |v: &str| -> PathBuf { cartella.join(VERSIONS_DIR).join(v).join(APP_EXE) };
    let versione = if !scelta.is_empty() && app_di(&scelta).exists() {
        scelta
    } else {
        match ultima_versione(&cartella) {
            Some(v) => {
                segnala_errore(&cartella, &testo(format!("{CURRENT_FILE} mancante o non valido ('{scelta}'): uso la versione {v}"),
                    format!("{CURRENT_FILE} missing or not valid ('{scelta}'): using version {v}")));
                v
            }
            None => {
                segnala_errore(&cartella, &testo(format!("Nessuna versione di Lemora trovata in {}", cartella.join(VERSIONS_DIR).display()),
                    format!("No Lemora version found in {}", cartella.join(VERSIONS_DIR).display())));
                return;
            }
        }
    };

    let app = app_di(&versione);
    #[cfg(windows)]
    if std::hint::black_box(ELEVATED_MARKER).is_empty() == false && argomenti.first().map(String::as_str) == Some(ELEVATED_FLAG) {
        let attesa = argomenti.get(1).map(String::as_str) == Some("wait");
        let resto: Vec<String> = argomenti.iter().skip(2).cloned().collect();
        std::process::exit(avvio_elevato(&cartella, app.parent().unwrap_or(&cartella), attesa, &resto));
    }
    if let Err(e) = Command::new(&app).args(&argomenti).current_dir(app.parent().unwrap_or(&cartella)).spawn() {
        segnala_errore(&cartella, &testo(format!("Avvio di {} non riuscito: {e}", app.display()), format!("Start of {} failed: {e}", app.display())));
    }
}
