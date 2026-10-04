pub fn encode(data: &[u8]) -> String {
    hex::encode(data)
}

pub fn decode(s: &str) -> Result<Vec<u8>, &'static str> {
    let clean = s.strip_prefix("0x").unwrap_or(s);
    hex::decode(clean).map_err(|_| "Invalid hex string")
}
