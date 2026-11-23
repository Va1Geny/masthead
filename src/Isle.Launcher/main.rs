#![windows_subsystem = "windows"]

use std::{
    env,
    ffi::OsString,
    io,
    process::{self, Command},
};

fn main() {
    let args: Vec<_> = env::args_os().skip(1).collect();
    let wait = args.first().is_some_and(|arg| arg == "--snapshot");

    match launch(&args, wait) {
        Ok(0) => (),
        Ok(code) => process::exit(code),
        Err(error) => {
            let message = format!("Could not start Isle.exe beside the launcher.\n\n{error}");
            if wait {
                eprintln!("{message}");
            } else {
                show_error(&message);
            }
            process::exit(1);
        }
    }
}

fn launch(args: &[OsString], wait: bool) -> io::Result<i32> {
    let exe = env::current_exe()?.with_file_name("Isle.exe");
    let mut app = Command::new(exe);
    app.args(args);

    if wait {
        Ok(app.status()?.code().unwrap_or(1))
    } else {
        app.spawn()?;
        Ok(0)
    }
}

#[link(name = "user32")]
unsafe extern "system" {
    fn MessageBoxW(
        owner: *mut core::ffi::c_void,
        text: *const u16,
        title: *const u16,
        flags: u32,
    ) -> i32;
}

fn show_error(message: &str) {
    const MB_ICONERROR: u32 = 0x10;
    let text: Vec<_> = message.encode_utf16().chain([0]).collect();
    let title: Vec<_> = "Isle".encode_utf16().chain([0]).collect();
    // Both strings are null-terminated and stay alive until the dialog closes.
    unsafe {
        MessageBoxW(
            core::ptr::null_mut(),
            text.as_ptr(),
            title.as_ptr(),
            MB_ICONERROR,
        )
    };
}
