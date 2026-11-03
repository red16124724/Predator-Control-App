//! Framing and sanitisation for the secure named-pipe IPC channel.
//!
//! Ports the message-shaping rules from `SecureNamedPipeIpc`. The pipe objects,
//! ACLs and impersonation checks stay managed because they are
//! `System.IO.Pipes` / `WindowsIdentity` API surface; the untrusted-input
//! handling lives here so no managed parser ever sees unbounded data.

use std::slice;

/// Hard ceiling on a pipe message, matching the managed constant.
pub const MAX_MESSAGE_LENGTH: usize = 256;
/// Longest payload after framing overhead.
pub const MAX_PAYLOAD_LENGTH: usize = MAX_MESSAGE_LENGTH - 2;

/// Removes CRs, converts LFs to spaces, strips NULs, trims and truncates.
///
/// This is the exact transformation the client applies before writing a
/// message and the server applies after reading one.
pub fn sanitize(message: &str) -> String {
    let mut out = String::with_capacity(message.len().min(MAX_PAYLOAD_LENGTH));
    for ch in message.chars() {
        let mapped = match ch {
            '\r' => None,
            '\n' => Some(' '),
            '\0' => None,
            _ => Some(ch),
        };
        if let Some(c) = mapped {
            if out.len() + c.len_utf8() > MAX_PAYLOAD_LENGTH {
                break;
            }
            out.push(c);
        }
    }
    out.trim().to_string()
}

/// True when a received line is a well-formed, non-empty message.
pub fn is_valid(message: &str) -> bool {
    let trimmed = message.trim_matches(|c: char| c == '\0' || c.is_whitespace());
    !trimmed.is_empty()
        && trimmed.len() <= MAX_MESSAGE_LENGTH
        && !trimmed.contains('\r')
        && !trimmed.contains('\n')
        && !trimmed.contains('\0')
}

/// Sanitises into `out`, returning the byte length or a negative error.
///
/// # Safety
/// `input` must be valid for `input_len` bytes; `out` for `out_cap` bytes.
pub unsafe fn sanitize_into(input: *const u8, input_len: i32, out: *mut u8, out_cap: i32) -> i32 {
    if out.is_null() || out_cap <= 0 {
        return -1;
    }
    let text = if input.is_null() || input_len <= 0 {
        ""
    } else {
        std::str::from_utf8(slice::from_raw_parts(input, input_len as usize)).unwrap_or("")
    };

    let clean = sanitize(text);
    let bytes = clean.as_bytes();
    if bytes.len() > out_cap as usize {
        return -1;
    }
    slice::from_raw_parts_mut(out, bytes.len()).copy_from_slice(bytes);
    bytes.len() as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_carriage_returns() {
        assert_eq!(sanitize("a\rb"), "ab");
    }

    #[test]
    fn converts_newlines_to_spaces() {
        assert_eq!(sanitize("a\nb"), "a b");
    }

    #[test]
    fn strips_nul_bytes() {
        assert_eq!(sanitize("a\0b"), "ab");
    }

    #[test]
    fn trims_surrounding_whitespace() {
        assert_eq!(sanitize("   set-mode turbo   "), "set-mode turbo");
    }

    #[test]
    fn truncates_to_payload_limit() {
        let long = "x".repeat(1000);
        assert_eq!(sanitize(&long).len(), MAX_PAYLOAD_LENGTH);
    }

    #[test]
    fn multibyte_truncation_stays_on_boundaries() {
        let long = "é".repeat(400);
        let out = sanitize(&long);
        assert!(out.len() <= MAX_PAYLOAD_LENGTH);
        // Must remain valid UTF-8, i.e. no split code point.
        assert!(std::str::from_utf8(out.as_bytes()).is_ok());
    }

    #[test]
    fn multibyte_truncation_never_exceeds_max_payload() {
        // 253 bytes of ASCII followed by 4-byte emoji: must stop at 253, not expand to 257
        let mut tricky = "a".repeat(MAX_PAYLOAD_LENGTH - 1);
        tricky.push('🎮');
        let out = sanitize(&tricky);
        assert!(out.len() <= MAX_PAYLOAD_LENGTH);
        assert_eq!(out.len(), MAX_PAYLOAD_LENGTH - 1);
    }

    #[test]
    fn validates_clean_messages() {
        assert!(is_valid("set-mode turbo"));
        assert!(is_valid("a"));
    }

    #[test]
    fn rejects_empty_or_control_messages() {
        assert!(!is_valid(""));
        assert!(!is_valid("   "));
        assert!(!is_valid("\0"));
        assert!(!is_valid("a\rb"));
        assert!(!is_valid("a\nb"));
        assert!(!is_valid("a\0b"));
    }

    #[test]
    fn rejects_oversized_messages() {
        assert!(!is_valid(&"x".repeat(MAX_MESSAGE_LENGTH + 1)));
    }

    #[test]
    fn sanitises_into_buffer() {
        let mut buf = [0u8; 64];
        let input = b"line1\r\nline2";
        let n = unsafe { sanitize_into(input.as_ptr(), input.len() as i32, buf.as_mut_ptr(), 64) };
        assert!(n > 0);
        assert_eq!(&buf[..n as usize], b"line1 line2");
    }

    #[test]
    fn sanitise_handles_null_input() {
        let mut buf = [0u8; 8];
        let n = unsafe { sanitize_into(std::ptr::null(), 0, buf.as_mut_ptr(), 8) };
        assert_eq!(n, 0);
        let n = unsafe { sanitize_into(std::ptr::null(), 0, std::ptr::null_mut(), 8) };
        assert_eq!(n, -1);
    }

    #[test]
    fn sanitise_reports_overflow() {
        let mut buf = [0u8; 4];
        let input = b"abcdefgh";
        let n = unsafe { sanitize_into(input.as_ptr(), input.len() as i32, buf.as_mut_ptr(), 4) };
        assert_eq!(n, -1);
    }

    #[test]
    fn invalid_utf8_degrades_to_empty() {
        let mut buf = [0u8; 8];
        let bad = [0xFFu8, 0xFE];
        let n = unsafe { sanitize_into(bad.as_ptr(), 2, buf.as_mut_ptr(), 8) };
        assert_eq!(n, 0);
    }
}
