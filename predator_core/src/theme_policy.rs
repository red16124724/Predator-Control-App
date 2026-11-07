//! Theme policy: power-mode indicator palette. WinForms colour objects,
//! theme detection and the accent colour stay managed; only the mode→RGB
//! table is native so the palette has one authoritative definition.

/// Packed 0xRRGGBB for a power-mode byte. Returns -1 when the caller must fall
/// back to the themed accent colour.
pub fn mode_color(mode: u8) -> i32 {
    match mode {
        0x00 => 0xFF_FFFF,
        0x01 => 0x00_96FF,
        0x04 => 0xA8_55F7,
        0x05 => 0xFF_2D87,
        0x06 => 0x00_FF78,
        _ => -1,
    }
}

/// `#RRGGBB` for a packed colour, used by the hex pill helpers.
pub fn packed_to_hex(color: i32) -> String {
    format!("#{:06X}", (color as u32) & 0x00FF_FFFF)
}

#[no_mangle]
pub unsafe extern "C" fn predator_theme_mode_color(mode: i32) -> i32 {
    crate::safe_ffi(-1, || {
        if !(0..=255).contains(&mode) {
            return -1;
        }
        mode_color(mode as u8)
    })
}

/// Writes `#RRGGBB` for a packed colour and returns its length, or -3 when the
/// output buffer is too small.
#[no_mangle]
pub unsafe extern "C" fn predator_theme_packed_to_hex(
    color: i32,
    out_ptr: *mut u8,
    out_cap: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        let text = packed_to_hex(color);
        if text.len() > out_cap || !crate::is_valid_buffer_range(out_ptr, text.len()) {
            return -3;
        }
        unsafe { std::ptr::copy_nonoverlapping(text.as_ptr(), out_ptr, text.len()) };
        text.len() as i32
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn palette_matches_documented_modes() {
        assert_eq!(mode_color(0x00), 0xFFFFFF);
        assert_eq!(mode_color(0x01), 0x0096FF);
        assert_eq!(mode_color(0x04), 0xA855F7);
        assert_eq!(mode_color(0x05), 0xFF2D87);
        assert_eq!(mode_color(0x06), 0x00FF78);
    }

    #[test]
    fn unknown_mode_defers_to_accent() {
        assert_eq!(mode_color(0x02), -1);
        assert_eq!(mode_color(0xFF), -1);
    }

    #[test]
    fn hex_rendering_is_zero_padded_uppercase() {
        assert_eq!(packed_to_hex(0x0096FF), "#0096FF");
        assert_eq!(packed_to_hex(-1), "#FFFFFF");
    }
}
