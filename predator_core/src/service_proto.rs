//! Wire protocol for the local Acer service sockets: packet framing,
//! response parsing and effect-name canonicalisation. Socket ownership,
//! connect policy and timeouts stay managed.

pub const MAGIC: [u8; 4] = *b"ACER";
pub const HEADER_LEN: usize = 8;

/// Builds an `ACER` + u32 LE packet id + payload frame.
pub fn build_packet(packet_id: u32, payload: &[u8]) -> Vec<u8> {
    let mut packet = Vec::with_capacity(HEADER_LEN + payload.len());
    packet.extend_from_slice(&MAGIC);
    packet.extend_from_slice(&packet_id.to_le_bytes());
    packet.extend_from_slice(payload);
    packet
}

/// Returns the payload slice when the frame starts with the magic, otherwise
/// `None` (the managed client treats that as a failed exchange).
pub fn response_payload(frame: &[u8]) -> Option<&[u8]> {
    if frame.len() < HEADER_LEN || frame[..4] != MAGIC {
        return None;
    }
    Some(&frame[HEADER_LEN..])
}

/// Uppercases and strips everything but `A-Z`, `0-9` and `_`, falling back to
/// `STATIC` for an empty result.
pub fn normalize_effect(name: &str) -> String {
    let mut out = String::with_capacity(name.len());
    for ch in name.trim().chars() {
        let upper = ch.to_ascii_uppercase();
        if upper.is_ascii_uppercase() || upper.is_ascii_digit() || upper == '_' {
            out.push(upper);
        }
    }
    if out.is_empty() {
        "STATIC".to_string()
    } else {
        out
    }
}

/// Reconnect back-off: 1 s doubling to a 5 s ceiling (managed PDH policy).
pub fn next_backoff_ms(current_ms: u32) -> u32 {
    current_ms.saturating_mul(2).min(5000)
}

#[no_mangle]
pub unsafe extern "C" fn predator_service_build_packet(
    packet_id: u32,
    payload_ptr: *const u8,
    payload_len: usize,
    out_ptr: *mut u8,
    out_cap: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if payload_len > 1 << 20 {
            return -2;
        }
        if payload_len > 0
            && (payload_ptr.is_null() || !crate::is_valid_buffer_range(payload_ptr, payload_len))
        {
            return -1;
        }
        let payload = if payload_len == 0 {
            &[][..]
        } else {
            unsafe { std::slice::from_raw_parts(payload_ptr, payload_len) }
        };
        let packet = build_packet(packet_id, payload);
        if packet.len() > out_cap || !crate::is_valid_buffer_range(out_ptr, packet.len()) {
            return -3;
        }
        unsafe { std::ptr::copy_nonoverlapping(packet.as_ptr(), out_ptr, packet.len()) };
        packet.len() as i32
    })
}

/// Returns the payload length and writes its offset into `out_offset`, or -1
/// when the frame is not an Acer response.
#[no_mangle]
pub unsafe extern "C" fn predator_service_response_payload(
    frame_ptr: *const u8,
    frame_len: usize,
    out_offset: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if frame_ptr.is_null()
            || frame_len == 0
            || frame_len > 1 << 20
            || !crate::is_valid_buffer_range(frame_ptr, frame_len)
            || !crate::is_valid_mut_ptr(out_offset)
        {
            return -1;
        }
        let frame = unsafe { std::slice::from_raw_parts(frame_ptr, frame_len) };
        match response_payload(frame) {
            Some(payload) => {
                unsafe { *out_offset = HEADER_LEN as i32 };
                payload.len() as i32
            }
            None => -1,
        }
    })
}

/// Writes the canonical effect name as UTF-8 and returns its length, or -3
/// when the caller's buffer is too small.
#[no_mangle]
pub unsafe extern "C" fn predator_service_normalize_effect(
    name_ptr: *const u8,
    name_len: usize,
    out_ptr: *mut u8,
    out_cap: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if name_len > 0
            && (name_ptr.is_null() || !crate::is_valid_buffer_range(name_ptr, name_len))
        {
            return -1;
        }
        let bytes = if name_len == 0 {
            &[][..]
        } else {
            unsafe { std::slice::from_raw_parts(name_ptr, name_len) }
        };
        let text = match std::str::from_utf8(bytes) {
            Ok(value) => value,
            Err(_) => return -2,
        };
        let normalized = normalize_effect(text);
        if normalized.len() > out_cap || !crate::is_valid_buffer_range(out_ptr, normalized.len()) {
            return -3;
        }
        unsafe { std::ptr::copy_nonoverlapping(normalized.as_ptr(), out_ptr, normalized.len()) };
        normalized.len() as i32
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_service_next_backoff_ms(current_ms: u32) -> u32 {
    crate::safe_ffi(u32::MAX, || next_backoff_ms(current_ms))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn packet_has_magic_id_and_payload() {
        let packet = build_packet(100, b"{}");
        assert_eq!(&packet[..4], b"ACER");
        assert_eq!(u32::from_le_bytes(packet[4..8].try_into().unwrap()), 100);
        assert_eq!(&packet[8..], b"{}");
        assert_eq!(packet.len(), HEADER_LEN + 2);
    }

    #[test]
    fn packet_with_empty_payload_is_header_only() {
        assert_eq!(build_packet(7, b"").len(), HEADER_LEN);
    }

    #[test]
    fn response_requires_magic() {
        assert_eq!(response_payload(b"ACER\x64\x00\x00\x00{\"Status\":0}"), Some(&b"{\"Status\":0}"[..]));
        assert_eq!(response_payload(b"NOPE\x00\x00\x00\x00{}"), None);
        assert_eq!(response_payload(b"ACER"), None);
    }

    #[test]
    fn normalize_strips_symbols_and_uppercases() {
        assert_eq!(normalize_effect(" breathing "), "BREATHING");
        assert_eq!(normalize_effect("neon-wave 2"), "NEONWAVE2");
        assert_eq!(normalize_effect("---"), "STATIC");
        assert_eq!(normalize_effect(""), "STATIC");
    }

    #[test]
    fn backoff_doubles_to_ceiling() {
        assert_eq!(next_backoff_ms(1000), 2000);
        assert_eq!(next_backoff_ms(3000), 5000);
        assert_eq!(next_backoff_ms(5000), 5000);
        assert_eq!(next_backoff_ms(u32::MAX), 5000);
    }
}
