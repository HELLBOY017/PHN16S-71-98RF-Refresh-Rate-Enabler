"""Unit tests for HDR certification (VESA DisplayHDR True Black 500) settings and GUIDs."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# Official VESA DisplayHDR True Black 500 GUIDs reverse-engineered from dxgkrnl.sys / SettingsHandlers_PCDisplay.dll
TRUE_BLACK_500_GUIDS = [
    "3B6DAA9E-3794-4D85-897E-93AE990D275D",  # CTS 1.2 Table 1 (Flag 0x4000)
    "86413F8C-0CDB-4D49-81F7-06BB64A8FED1",  # CTS 1.1 Table 1 (Flag 0x40)
    "E7E7349F-EF0A-4EF7-A561-C8F633F27463",  # CTS 1.2 Table 2 (Flag 0x4000000000)
]

GUID_REGEX = re.compile(r"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$")


class HdrCertificationTests(unittest.TestCase):
    def test_guid_formats(self):
        for guid in TRUE_BLACK_500_GUIDS:
            with self.subTest(guid=guid):
                self.assertIsNotNone(GUID_REGEX.match(guid))
                self.assertEqual(len(guid), 36)

    def test_program_cs_contains_true_black_guids(self):
        program_cs = (ROOT / "Program.cs").read_text(encoding="utf-8")
        for guid in TRUE_BLACK_500_GUIDS:
            with self.subTest(guid=guid):
                self.assertIn(guid, program_cs)

    def test_monitor_data_store_subkeys_format(self):
        expected_keys = [
            "SDC41AB0_00_07E7_FE",  # Stock Samsung panel EDID (NVIDIA/Intel)
            "SDC41AB0_00_07E7_FC",  # Staged Samsung panel EDID (4 blocks)
            "SDC41790_00_07E7_FE",  # Intel DRR spoofed panel EDID (legacy)
            "SDC41790_00_07E7_FC",  # Intel DRR spoofed staged EDID (legacy)
            "SAM100A0_00_07E7_FE",  # Intel DRR 48-240Hz spoofed panel EDID
            "SAM100A0_00_07E7_FC",  # Intel DRR 48-240Hz spoofed staged EDID
        ]
        program_cs = (ROOT / "Program.cs").read_text(encoding="utf-8")
        for key in expected_keys:
            with self.subTest(key=key):
                self.assertIn(key, program_cs)

    def test_monitor_class_driver_key_target(self):
        """Verify that Program.cs targets the monitor Class software/driver key read by dxgkrnl.sys."""
        program_cs = (ROOT / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("{4d36e96e-e325-11ce-bfc1-08002be10318}", program_cs)
        self.assertIn("Control\\Class\\{4d36e96e-e325-11ce-bfc1-08002be10318}", program_cs)

    def test_monitor_device_parameters_target(self):
        """Verify that Program.cs targets the monitor hardware Device Parameters keys."""
        program_cs = (ROOT / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("Enum\\DISPLAY", program_cs)
        self.assertIn("Device Parameters", program_cs)

    def test_driver_restart_has_restart_monitors(self):
        """Verify that DriverRestart has RestartMonitors for instant PnP arrival notification."""
        driver_restart_cs = (ROOT / "DriverRestart.cs").read_text(encoding="utf-8")
        self.assertIn("RestartMonitors", driver_restart_cs)
        self.assertIn("4d36e96e-e325-11ce-bfc1-08002be10318", driver_restart_cs)

    def test_main_window_has_restore_hdr_button(self):
        main_window_cs = (ROOT / "MainWindow.cs").read_text(encoding="utf-8")
        self.assertIn("Restore HDR certification", main_window_cs)
        self.assertIn("RestoreHdrClick", main_window_cs)


if __name__ == "__main__":
    unittest.main()
