"""Unit tests for NVIDIA EDID override generation."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('enabler', ROOT / 'phn16s_refresh_enabler.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

with open(ROOT / 'tests/test_intel_edid.py', 'r', encoding='utf-8') as f:
    for line in f:
        if 'ATNA60DL03-0' in line:
            STOCK_EDID = eval(line.strip()[line.strip().find("b'"):])
            break


def block_checksum(data, offset=0):
    s = sum(data[offset:offset + 127]) & 0xFF
    return (256 - s) & 0xFF


def encode_dtd(width, height, pclk_khz, hfp, hsw, hbp, vfp, vsw, vbp, hpos=False, vpos=False):
    dtd = bytearray(18)
    pclk_10k = int(round(pclk_khz / 10.0))
    dtd[0] = pclk_10k & 0xFF
    dtd[1] = (pclk_10k >> 8) & 0xFF
    hblank = hfp + hsw + hbp
    dtd[2] = width & 0xFF
    dtd[3] = hblank & 0xFF
    dtd[4] = (((width >> 8) & 0x0F) << 4) | ((hblank >> 8) & 0x0F)
    vblank = vfp + vsw + vbp
    dtd[5] = height & 0xFF
    dtd[6] = vblank & 0xFF
    dtd[7] = (((height >> 8) & 0x0F) << 4) | ((vblank >> 8) & 0x0F)
    dtd[8] = hfp & 0xFF
    dtd[9] = hsw & 0xFF
    dtd[10] = ((vfp & 0x0F) << 4) | (vsw & 0x0F)
    dtd[11] = (((hfp >> 8) & 0x03) << 6) | (((hsw >> 8) & 0x03) << 4) | (((vfp >> 4) & 0x03) << 2) | ((vsw >> 4) & 0x03)
    hsize_mm = 344
    vsize_mm = 215
    dtd[12] = hsize_mm & 0xFF
    dtd[13] = vsize_mm & 0xFF
    dtd[14] = (((hsize_mm >> 8) & 0x0F) << 4) | ((vsize_mm >> 8) & 0x0F)
    flags = 0x18
    if vpos:
        flags |= 0x04
    if hpos:
        flags |= 0x02
    dtd[17] = flags
    return bytes(dtd)


def decode_dtd(dtd):
    pclk = (dtd[0] | (dtd[1] << 8)) * 10
    h_act = dtd[2] | ((dtd[4] >> 4) << 8)
    h_bl = dtd[3] | ((dtd[4] & 0x0F) << 8)
    v_act = dtd[5] | ((dtd[7] >> 4) << 8)
    v_bl = dtd[6] | ((dtd[7] & 0x0F) << 8)
    h_fp = dtd[8] | (((dtd[11] >> 6) & 0x03) << 8)
    h_sw = dtd[9] | (((dtd[11] >> 4) & 0x03) << 8)
    v_fp = (dtd[10] >> 4) | (((dtd[11] >> 2) & 0x03) << 4)
    v_sw = (dtd[10] & 0x0F) | ((dtd[11] & 0x03) << 4)
    htot = h_act + h_bl
    vtot = v_act + v_bl
    hz = (pclk * 1000.0) / (htot * vtot) if htot * vtot > 0 else 0
    return {
        'pclk_khz': pclk,
        'width': h_act,
        'height': v_act,
        'htot': htot,
        'vtot': vtot,
        'hz': hz,
        'hfp': h_fp,
        'hsw': h_sw,
        'vfp': v_fp,
        'vsw': v_sw,
    }


def build_displayid_extension(timings, following_sections=0):
    b = bytearray(128)
    b[0] = 0x70
    b[1] = 0x20
    b[2] = 121
    b[3] = 0x00
    b[4] = following_sections
    b[5] = 0x22
    b[6] = 0x00
    b[7] = 20 * len(timings)
    offset = 8
    for t in timings:
        b[offset:offset + 20] = t
        offset += 20
    s = sum(b[1:126]) & 0xFF
    b[126] = (256 - s) & 0xFF
    b[127] = block_checksum(b, 0)
    return bytes(b)


class NvidiaEdidTests(unittest.TestCase):
    def test_dtd_encodes_and_decodes_accurately(self):
        dtd60 = encode_dtd(2560, 1600, 281318, 8, 32, 40, 8, 8, 160)
        dec60 = decode_dtd(dtd60)
        self.assertEqual(dec60['width'], 2560)
        self.assertEqual(dec60['height'], 1600)
        self.assertEqual(dec60['htot'], 2640)
        self.assertEqual(dec60['vtot'], 1776)
        self.assertAlmostEqual(dec60['hz'], 60.0, places=1)

        dtd75 = encode_dtd(2560, 1600, 351648, 8, 32, 40, 8, 8, 160)
        dec75 = decode_dtd(dtd75)
        self.assertAlmostEqual(dec75['hz'], 75.0, places=1)

        dtd100 = encode_dtd(2560, 1600, 468864, 8, 32, 40, 8, 8, 160)
        dec100 = decode_dtd(dtd100)
        self.assertAlmostEqual(dec100['hz'], 100.0, places=1)

        dtd120 = encode_dtd(2560, 1600, 562637, 8, 32, 40, 8, 8, 160)
        dec120 = decode_dtd(dtd120)
        self.assertAlmostEqual(dec120['hz'], 120.0, places=1)

    def test_nvidia_block0_descriptors_remain_zeros_to_prevent_black_screen(self):
        stock_block0 = bytearray(128)
        self.assertEqual(stock_block0[54:72], b'\x00' * 18)
        self.assertEqual(stock_block0[90:108], b'\x00' * 18)

    def test_full_override_includes_all_refresh_rates_and_metadata(self):
        edid = module.generate_overridden_edid(STOCK_EDID)
        self.assertEqual(len(edid), 512)

        # Block 0: extension count = 3
        b0 = edid[:128]
        self.assertEqual(b0[126], 3)
        self.assertEqual(sum(b0) % 256, 0)
        self.assertEqual(b0[54:72], b'\x00' * 18)
        self.assertEqual(b0[90:108], b'\x00' * 18)

        # Block 1: Tag 0x20, 0x21, 0x26 (eDP), 0x22 (240Hz), 0x81 (HDR), 0x2B (VRR)
        b1 = edid[128:256]
        self.assertEqual(b1[0], 0x70)
        self.assertEqual(b1[1], 0x20)
        self.assertEqual(b1[2], 121)
        self.assertEqual(b1[4], 2)
        self.assertEqual(sum(b1) % 256, 0)
        self.assertEqual(sum(b1[1:127]) % 256, 0)

        # Verify Tag 0x26 (Display Interface Features: eDP) is present
        self.assertIn(b'\x26\x00\x09\x04', bytes(b1))

        # Block 2: 5 timings (60, 75, 100, 120, 144 Hz)
        b2 = edid[256:384]
        self.assertEqual(b2[0], 0x70)
        self.assertEqual(b2[1], 0x20)
        self.assertEqual(b2[2], 121)
        self.assertEqual(b2[4], 1)
        self.assertEqual(sum(b2) % 256, 0)
        self.assertEqual(sum(b2[1:127]) % 256, 0)

        # Block 3: 1 timing (165 Hz)
        b3 = edid[384:512]
        self.assertEqual(b3[0], 0x70)
        self.assertEqual(b3[1], 0x20)
        self.assertEqual(b3[2], 121)
        self.assertEqual(b3[4], 0)
        self.assertEqual(sum(b3) % 256, 0)
        self.assertEqual(sum(b3[1:127]) % 256, 0)

        # Verify all 7 refresh rates are present: 60, 75, 100, 120, 144, 165, 240 Hz
        all_timings = []
        extensions = min(edid[126], (len(edid) // 128) - 1)
        for ext in range(1, extensions + 1):
            origin = ext * 128
            offset = origin + 5
            length = edid[origin + 2]
            end = min(offset + length, origin + 127)
            while offset + 3 <= end:
                tag = edid[offset]
                payload = edid[offset + 2]
                if tag == 0 and payload == 0:
                    break
                if offset + 3 + payload > end:
                    break
                if tag == 0x22:
                    count = payload // 20
                    for i in range(count):
                        d = bytes(edid[offset + 3 + i * 20:offset + 3 + (i + 1) * 20])
                        all_timings.append(d)
                offset += payload + 3

        self.assertEqual(len(all_timings), 7)
        rates = []
        for t in all_timings:
            clk, opt, ha, hb, hfp, hsw, hpos, va, vb, vfp, vsw, vpos = module.decode_type_vii(t)
            fps = round((clk * 1000.0) / ((ha + hb) * (va + vb)))
            rates.append(fps)
        self.assertEqual(sorted(rates), [60, 75, 100, 120, 144, 165, 240])


if __name__ == '__main__':
    unittest.main()
