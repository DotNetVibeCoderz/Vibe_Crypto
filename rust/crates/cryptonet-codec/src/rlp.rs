//! Recursive Length Prefix encoding (Ethereum yellow paper, appendix B).

pub fn encode_bytes(data: &[u8]) -> Vec<u8> {
    if data.len() == 1 && data[0] < 0x80 {
        return vec![data[0]];
    }
    let mut out = Vec::with_capacity(9 + data.len());
    write_header(&mut out, 0x80, data.len());
    out.extend_from_slice(data);
    out
}

/// Encodes a list whose items are already RLP-encoded.
pub fn encode_list(items: &[&[u8]]) -> Vec<u8> {
    let total: usize = items.iter().map(|i| i.len()).sum();
    let mut out = Vec::with_capacity(9 + total);
    write_header(&mut out, 0xc0, total);
    for item in items {
        out.extend_from_slice(item);
    }
    out
}

/// Encodes an unsigned big-endian integer (leading zeros stripped).
pub fn encode_uint(be: &[u8]) -> Vec<u8> {
    let start = be.iter().position(|&b| b != 0).unwrap_or(be.len());
    encode_bytes(&be[start..])
}

fn write_header(out: &mut Vec<u8>, offset: u8, len: usize) {
    if len <= 55 {
        out.push(offset + len as u8);
    } else {
        let len_be = len.to_be_bytes();
        let start = len_be
            .iter()
            .position(|&b| b != 0)
            .unwrap_or(len_be.len() - 1);
        out.push(offset + 55 + (len_be.len() - start) as u8);
        out.extend_from_slice(&len_be[start..]);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn yellow_paper_vectors() {
        assert_eq!(encode_bytes(b"dog"), vec![0x83, b'd', b'o', b'g']);
        assert_eq!(encode_bytes(b""), vec![0x80]);
        assert_eq!(encode_uint(&[0, 0]), vec![0x80]);
        assert_eq!(encode_uint(&[0x0f]), vec![0x0f]);
        assert_eq!(encode_uint(&[0x04, 0x00]), vec![0x82, 0x04, 0x00]);
        let cat = encode_bytes(b"cat");
        let dog = encode_bytes(b"dog");
        assert_eq!(
            encode_list(&[&cat, &dog]),
            vec![0xc8, 0x83, b'c', b'a', b't', 0x83, b'd', b'o', b'g']
        );
        assert_eq!(encode_list(&[]), vec![0xc0]);
        let long = b"Lorem ipsum dolor sit amet, consectetur adipisicing elit";
        assert_eq!(&encode_bytes(long)[..2], &[0xb8, 0x38]);
    }
}
