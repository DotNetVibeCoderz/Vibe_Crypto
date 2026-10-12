"""Starts Trezor's core emulator headless, loads the public BIP-39 test phrase and keeps it running.

Used by CI for the Trezor end-to-end tests. Needs `pip install trezor` and an emulator binary from
https://data.trezor.io/dev/firmware/releases/emulators-new/.

    python scripts/trezor-emulator.py <emulator-binary> <profile-dir>

Writes <profile-dir>/ready once the device is loaded. The main interface is UDP 21324, the debug link UDP 21325.

Built by Gravicode Studios, led by Kang Fadhil.
"""
import sys
import time
from pathlib import Path

from trezorlib._internal.emulator import CoreEmulator
from trezorlib.debuglink import load_device

TEST_PHRASE = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"

binary, profile = Path(sys.argv[1]), Path(sys.argv[2])
profile.mkdir(parents=True, exist_ok=True)
emulator = CoreEmulator(binary, str(profile), headless=True, logfile=str(profile / "emulator.log"))
emulator.start()
load_device(emulator.client, mnemonic=TEST_PHRASE, pin=None, passphrase_protection=False, label="Crypto.Net CI")
features = emulator.client.features
print(f"Trezor emulator ready: {features.model} {features.major_version}.{features.minor_version}.{features.patch_version}", flush=True)
emulator.client.close()   # free the interface for the tests; the process keeps running
(profile / "ready").write_text("ok")
while emulator.process.poll() is None:
    time.sleep(5)
print("emulator exited", emulator.process.returncode, flush=True)
