fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let mut res = tauri_winres::WindowsResource::new();
        res.set_icon("../../Ui/LOGO.ico");
        // explicit metadata: an exe without description or name is more easily flagged as suspicious by antivirus software
        res.set("ProductName", "Lemora");
        res.set("FileDescription", "Lemora");
        res.set("OriginalFilename", "Lemora.exe");
        res.set("InternalName", "Lemora");
        res.compile().expect("cannot embed the icon in the launcher");
    }
}
