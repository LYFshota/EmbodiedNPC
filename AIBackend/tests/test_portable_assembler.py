"""Portable resources must be self-contained and match the authored Unity menu."""
from pathlib import Path
import json, tempfile, unittest
from unittest.mock import patch
from portable import assemble as assembler

class PortableAssemblerTests(unittest.TestCase):
    def test_shared_snack_resource_is_copied_byte_identically(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); source=root/'source/AIBackend'
            (source/'app/knowledge').mkdir(parents=True)
            (source/'app/builtin_embedding_resources.json').write_text('{"files":["model.onnx"]}')
            catalog=source.parent/'UnityClient/Assets/Resources/CafeSnackCatalog.json'
            catalog.parent.mkdir(parents=True); payload='{"items":[{"id":"test","chinese":"测试"}]}'.encode('utf-8');catalog.write_bytes(payload)
            (source/'portable/root').mkdir(parents=True)
            for name in ('launcher.py','unified.py','start.cmd','stop.cmd','restart.cmd','README.md'):(source/'portable'/name).write_text('fixture')
            for name in ('Start.cmd','Stop.cmd'):(source/'portable/root'/name).write_text('fixture')
            for directory,name in (('python','python.exe'),('player','EmbodiedNPC.exe'),('models','model.onnx')):
                (root/directory).mkdir();(root/directory/name).write_bytes(b'fixture')
            with patch.object(assembler,'__file__',str(source/'portable/assemble.py')):
                result=assembler.assemble(root/'out',root/'python',root/'models',root/'player')
            self.assertEqual((result/'Backend/app/knowledge/cafe_snacks.json').read_bytes(),payload)
            self.assertFalse((result/'UserData').exists())

    def test_missing_catalog_fails_before_creating_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'source/AIBackend';(source/'portable').mkdir(parents=True)
            for directory,name in (('python','python.exe'),('player','EmbodiedNPC.exe')):
                (root/directory).mkdir();(root/directory/name).touch()
            with patch.object(assembler,'__file__',str(source/'portable/assemble.py')):
                with self.assertRaisesRegex(ValueError,'Shared snack catalog'):
                    assembler.assemble(root/'out',root/'python',root/'models',root/'player')
            self.assertFalse((root/'out').exists())
