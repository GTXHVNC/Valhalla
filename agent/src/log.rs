//! Conditional debug logging for Einherjar.
//!
//! # Build variants
//!
//! - **debug-log feature enabled** (`stub_debug.bin`): every `dbg_log!` call
//!   writes a formatted diagnostic line to stderr.  Use this build to trace
//!   the execution path and diagnose failures.
//!
//! - **debug-log feature disabled** (`stub.bin`): the macro expands to
//!   nothing.  All format arguments are still type-checked but the entire
//!   expression is elided; there is no runtime overhead.
//!
//! # Format
//!
//! ```text
//! [DEBUG] [Component] Specific event
//! ```
//!
//! # Security
//!
//! Never log passwords, authentication tokens, private keys, or other
//! credential material.

/// Emit a structured debug line to stderr when the `debug-log` feature is
/// active; a no-op otherwise.
///
/// Arguments are always type-checked by the compiler regardless of the
/// active feature so that mistakes are caught in both configurations.
#[macro_export]
macro_rules! dbg_log {
    ($fmt:literal $(, $arg:expr)* $(,)?) => {
        #[cfg(feature = "debug-log")]
        {
            eprintln!(concat!("[DEBUG] ", $fmt) $(, $arg)*);
        }
        #[cfg(not(feature = "debug-log"))]
        {
            // Consume the expressions for type-checking; the compiler will
            // dead-code-eliminate this block entirely in release builds.
            let _ = format_args!(concat!("[DEBUG] ", $fmt) $(, $arg)*);
        }
    };
}
