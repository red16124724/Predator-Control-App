//! Fixed-capacity telemetry history ring buffer.
//!
//! Ports `HistoryBuffer` from the managed application. The buffer is exposed to
//! C# through an opaque handle so the managed wrapper keeps its existing public
//! surface while the storage, indexing and reduction arithmetic run here.

use std::slice;

/// Minimum usable capacity.
pub const MIN_CAPACITY: i32 = 2;

/// A ring buffer of optional samples.
#[derive(Debug)]
pub struct HistoryBuffer {
    values: Vec<Option<f64>>,
    next: usize,
    count: usize,
}

impl HistoryBuffer {
    pub fn new(capacity: i32) -> Self {
        let cap = capacity.max(MIN_CAPACITY) as usize;
        Self {
            values: vec![None; cap],
            next: 0,
            count: 0,
        }
    }

    pub fn capacity(&self) -> usize {
        self.values.len()
    }

    pub fn count(&self) -> usize {
        self.count
    }

    pub fn add(&mut self, value: Option<f64>) {
        let cap = self.values.len();
        self.values[self.next] = value.filter(|v| v.is_finite() || v.is_nan());
        self.next = (self.next + 1) % cap;
        if self.count < cap {
            self.count += 1;
        }
    }

    /// Oldest-first physical index for logical position `index`.
    fn physical(&self, index: usize) -> usize {
        let cap = self.values.len();
        (self.next + cap - self.count + index) % cap
    }

    /// Logical access, oldest first.
    pub fn get(&self, index: i32) -> Option<f64> {
        if index < 0 || index as usize >= self.count {
            return None;
        }
        self.values[self.physical(index as usize)].filter(|v| v.is_finite())
    }

    /// Minimum and maximum of the finite samples, if any.
    pub fn range(&self) -> Option<(f64, f64)> {
        let mut min = f64::INFINITY;
        let mut max = f64::NEG_INFINITY;
        let mut seen = false;
        for i in 0..self.count {
            if let Some(v) = self.values[self.physical(i)] {
                if v.is_finite() {
                    min = min.min(v);
                    max = max.max(v);
                    seen = true;
                }
            }
        }
        if seen {
            Some((min, max))
        } else {
            None
        }
    }

    pub fn average(&self) -> Option<f64> {
        let mut sum = 0.0;
        let mut valid = 0usize;
        for i in 0..self.count {
            if let Some(v) = self.values[self.physical(i)] {
                if v.is_finite() {
                    sum += v;
                    valid += 1;
                }
            }
        }
        if valid > 0 {
            Some(sum / valid as f64)
        } else {
            None
        }
    }

    /// Latest finite sample.
    pub fn latest(&self) -> Option<f64> {
        if self.count == 0 {
            return None;
        }
        self.values[self.physical(self.count - 1)].filter(|v| v.is_finite())
    }

    pub fn clear(&mut self) {
        for slot in self.values.iter_mut() {
            *slot = None;
        }
        self.next = 0;
        self.count = 0;
    }

    /// Copies samples into `out` oldest-first, writing `f64::NAN` for gaps.
    ///
    /// When `out` is shorter than the sample count the most recent samples are
    /// copied, so a truncated graph still shows current data.
    pub fn snapshot_into(&self, out: &mut [f64]) -> usize {
        let n = self.count.min(out.len());
        let start = self.count - n;
        for (i, slot) in out.iter_mut().take(n).enumerate() {
            *slot = self.values[self.physical(start + i)].unwrap_or(f64::NAN);
        }
        n
    }
}

/// Owns a heap-allocated buffer for the C-ABI.
#[repr(C)]
pub struct HistoryHandle {
    pub buffer: HistoryBuffer,
}

/// Allocates a buffer. The caller owns the pointer and must call
/// [`history_destroy`].
pub fn create(capacity: i32) -> *mut HistoryHandle {
    Box::into_raw(Box::new(HistoryHandle {
        buffer: HistoryBuffer::new(capacity),
    }))
}

/// # Safety
/// `handle` must be a pointer returned by [`create`] and not already freed.
pub unsafe fn destroy(handle: *mut HistoryHandle) {
    if !handle.is_null() && (handle as usize) % std::mem::align_of::<HistoryHandle>() == 0 {
        drop(Box::from_raw(handle));
    }
}

/// # Safety
/// `handle` must be a live pointer from [`create`].
pub unsafe fn as_ref<'a>(handle: *const HistoryHandle) -> Option<&'a HistoryBuffer> {
    if handle.is_null() || (handle as usize) % std::mem::align_of::<HistoryHandle>() != 0 {
        return None;
    }
    handle.as_ref().map(|h| &h.buffer)
}

/// # Safety
/// `handle` must be a live pointer from [`create`].
pub unsafe fn as_mut<'a>(handle: *mut HistoryHandle) -> Option<&'a mut HistoryBuffer> {
    if handle.is_null() || (handle as usize) % std::mem::align_of::<HistoryHandle>() != 0 {
        return None;
    }
    handle.as_mut().map(|h| &mut h.buffer)
}

/// Writes all samples into `out` as `f64`, using `NaN` for gaps.
///
/// # Safety
/// `out` must be valid for `out_len` writes.
pub unsafe fn write_snapshot(buffer: &HistoryBuffer, out: *mut f64, out_len: i32) -> i32 {
    if out.is_null() || out_len <= 0 {
        return 0;
    }
    if (out as usize) % std::mem::align_of::<f64>() == 0 {
        let slice = slice::from_raw_parts_mut(out, out_len as usize);
        buffer.snapshot_into(slice) as i32
    } else {
        let mut temp = vec![f64::NAN; out_len as usize];
        let written = buffer.snapshot_into(&mut temp);
        for i in 0..written {
            std::ptr::write_unaligned(out.add(i), temp[i]);
        }
        written as i32
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capacity_is_floored() {
        assert_eq!(HistoryBuffer::new(0).capacity(), MIN_CAPACITY as usize);
        assert_eq!(HistoryBuffer::new(-5).capacity(), MIN_CAPACITY as usize);
    }

    #[test]
    fn fills_and_overwrites_in_order() {
        let mut b = HistoryBuffer::new(3);
        b.add(Some(1.0));
        b.add(Some(2.0));
        b.add(Some(3.0));
        assert_eq!(b.count(), 3);
        assert_eq!((b.get(0), b.get(1), b.get(2)), (Some(1.0), Some(2.0), Some(3.0)));

        b.add(Some(4.0));
        assert_eq!(b.count(), 3);
        assert_eq!((b.get(0), b.get(1), b.get(2)), (Some(2.0), Some(3.0), Some(4.0)));
    }

    #[test]
    fn indexing_out_of_range_is_none() {
        let b = HistoryBuffer::new(4);
        assert_eq!(b.get(0), None);
        assert_eq!(b.get(-1), None);
        let mut b = HistoryBuffer::new(4);
        b.add(Some(1.0));
        assert_eq!(b.get(1), None);
    }

    #[test]
    fn gaps_are_skipped_by_reductions() {
        let mut b = HistoryBuffer::new(5);
        b.add(Some(10.0));
        b.add(None);
        b.add(Some(f64::NAN));
        b.add(Some(20.0));
        assert_eq!(b.range(), Some((10.0, 20.0)));
        assert_eq!(b.average(), Some(15.0));
        assert_eq!(b.latest(), Some(20.0));
    }

    #[test]
    fn empty_buffer_has_no_reductions() {
        let b = HistoryBuffer::new(4);
        assert_eq!(b.range(), None);
        assert_eq!(b.average(), None);
        assert_eq!(b.latest(), None);
    }

    #[test]
    fn snapshot_is_oldest_first_with_nan_gaps() {
        let mut b = HistoryBuffer::new(3);
        b.add(Some(1.0));
        b.add(None);
        b.add(Some(3.0));
        let mut out = [0.0f64; 3];
        let n = b.snapshot_into(&mut out);
        assert_eq!(n, 3);
        assert_eq!(out[0], 1.0);
        assert!(out[1].is_nan());
        assert_eq!(out[2], 3.0);
    }

    #[test]
    fn snapshot_respects_short_output() {
        let mut b = HistoryBuffer::new(4);
        for v in 1..=4 {
            b.add(Some(v as f64));
        }
        let mut out = [0.0f64; 2];
        assert_eq!(b.snapshot_into(&mut out), 2);
        assert_eq!(out, [3.0, 4.0]);
    }

    #[test]
    fn clear_resets_everything() {
        let mut b = HistoryBuffer::new(4);
        b.add(Some(5.0));
        b.clear();
        assert_eq!(b.count(), 0);
        assert_eq!(b.get(0), None);
        assert_eq!(b.latest(), None);
    }

    #[test]
    fn handle_roundtrip() {
        unsafe {
            let h = create(3);
            {
                let buf = as_mut(h).unwrap();
                buf.add(Some(1.5));
                buf.add(Some(2.5));
            }
            assert_eq!(as_ref(h).unwrap().count(), 2);
            let mut out = [0.0f64; 3];
            let n = write_snapshot(as_ref(h).unwrap(), out.as_mut_ptr(), 3);
            assert_eq!(n, 2);
            assert_eq!(out[0], 1.5);
            assert_eq!(out[1], 2.5);
            destroy(h);
        }
    }

    #[test]
    fn null_handle_is_safe() {
        unsafe {
            assert!(as_ref(std::ptr::null()).is_none());
            assert!(as_mut(std::ptr::null_mut()).is_none());
            destroy(std::ptr::null_mut());
            assert_eq!(write_snapshot(&HistoryBuffer::new(2), std::ptr::null_mut(), 0), 0);
        }
    }
}
