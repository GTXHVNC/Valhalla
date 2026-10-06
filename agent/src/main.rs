mod args;
mod auth;
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
        return;
    }
    let args = args::get();
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(_) => std::process::exit(1),
    };
    if runtime.block_on(net::run(&args, final_ready)).is_err() {
        std::process::exit(1);
    }
}
