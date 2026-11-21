"""Exercise early-boot selection without accessing the machine's DRM settings."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
HOOK = ROOT / 'linux/dracut/91preysense-edid/intel-edid.sh'

class IntelEdidTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which('sh') is not None, "sh is required")
    def test_only_intel_primary_uses_override(self):
        functions = HOOK.read_text().rsplit('preysense_select_edid', 1)[0]
        for owner in ('intel', 'nvidia', None):
            with self.subTest(owner=owner), tempfile.TemporaryDirectory() as folder:
                for name,vendor in (('intel','0x8086'),('nvidia','0x10de')):
                    dev=Path(folder)/'bus/pci/devices'/name
                    dev.mkdir(parents=True)
                    (dev/'vendor').write_text(vendor+'\n')
                    (dev/'boot_vga').write_text(('1' if owner==name else '0')+'\n')
                result=subprocess.run(['sh','-c',functions+'\npreysense_intel_boot_display "$1"','test',folder])
                self.assertEqual(result.returncode==0,owner=='intel')

    def test_uninstall_does_not_require_connected_panel(self):
        spec=importlib.util.spec_from_file_location('enabler',ROOT/'phn16s_refresh_enabler.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with patch('sys.argv',['enabler','--uninstall']), patch.object(module,'uninstall_override') as uninstall, patch.object(module,'find_edp_connector') as detect:
            module.main()
        uninstall.assert_called_once()
        detect.assert_not_called()

    @unittest.skipUnless(hasattr(os, 'geteuid'), "POSIX geteuid required")
    def test_reject_nvidia_connector_before_installing(self):
        spec=importlib.util.spec_from_file_location('enabler',ROOT/'phn16s_refresh_enabler.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with patch.object(module.os,'geteuid',return_value=0),patch.object(module.os,'makedirs') as mkdir:
            with self.assertRaises(ValueError):module.install_override(b'', ['eDP-2'])
        mkdir.assert_not_called()

    def test_adaptive_sync_block_patched_to_240(self):
        spec=importlib.util.spec_from_file_location('enabler',ROOT/'phn16s_refresh_enabler.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        source = (
            b'\x00\xff\xff\xff\xff\xff\xff\x00L\x83\xabA\x00\x00\x00\x00\x00!\x01\x04\xb5"\x16x\x03\x0c\xf1\xaeR<\xb9#\x0cPT\x00\x00\x00\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\xfd\x0c0\xf0\xab\xabq\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\xfe\x00ATNA60DL03-0 \x01\xfep y\x02\x00 \x00\x0c\xbaAY\xabA\x00\x00\x00\x00\x00\x17\x00!\x00\x1dt\ri\x08\x00\n@\x06\x00\xe1\xfaQ\xcb\x13\xb9=\xd2\x0c\x01ET@^\xd0`\x18\x10#x&\x00\t\x04\x00\x00\x00\x00\x00P\x00\x00"\x00\x14\x9a+\x11\x85\xff\tO\x00\x07\x00\x1f\x00?\x06\xaf\x00\x9d\x00\x07\x00\x81\x00\x0b\xe3\x05\x80\x00\xe6\x06\x05\x01t`\x02+\x00\x06\'\x00<\xef\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\xf1\x90'
        )
        overridden = module.generate_overridden_edid(source)
        timings, others = module.collect_display_id(overridden)
        found_tag20 = False
        found_tag2b = False
        for o in others:
            if o[0] == 0x20:
                found_tag20 = True
                self.assertEqual(o[6:8], b'\x0a\x10')  # SAM 0x100A product code in DisplayID
            if o[0] == 0x2B:
                found_tag2b = True
                self.assertEqual(o[5], 0x30)  # 48 Hz Adaptive-Sync min rate
                self.assertEqual(o[6], 0xF0)  # 240 Hz Adaptive-Sync max rate
        self.assertTrue(found_tag20)
        self.assertTrue(found_tag2b)
        # Check Block 0 Manufacturer and Product Code spoofed to SAM 0x100A to match Intel driver whitelist table (entry 94)
        self.assertEqual(overridden[8:10], b'\x4c\x2d')
        self.assertEqual(overridden[10:12], b'\x0a\x10')
        # Check Block 0 descriptors 0 and 2 remain zero (no invalid scaled pixel-clock modes)
        self.assertEqual(overridden[54:72], b'\x00' * 18)
        self.assertEqual(overridden[90:108], b'\x00' * 18)
        self.assertEqual(module.block_checksum(overridden[:128], 0), overridden[127])
