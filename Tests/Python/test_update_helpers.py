import base64
import importlib.util
import plistlib
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'Scripts' / filename)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


config = module('update_config', 'configure-updates.py')
appcast = module('appcast_check', 'verify-appcast.py')


class ConfigurationTests(unittest.TestCase):
    def test_repository_url_and_slug_resolve_to_same_release_feed(self):
        expected = 'https://github.com/WaterBotol/Lunalyr/releases/latest/download/appcast.xml'
        self.assertEqual(config.github_feed('WaterBotol/Lunalyr'), expected)
        self.assertEqual(config.github_feed('https://github.com/WaterBotol/Lunalyr.git'), expected)
        self.assertEqual(config.github_feed('https://github.com/WaterBotol/Lunalyr.git/'), expected)

    def test_rejects_unexpected_repository_shapes(self):
        for value in ['owner', 'owner/repo/extra', 'https://other.example/owner/repo', 'owner/repo;command']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                config.github_feed(value)

    def test_rejects_insecure_feeds_and_wrong_length_keys(self):
        key = base64.b64encode(b'x' * 32).decode()
        for feed in ['http://example.com/appcast.xml', 'file:///tmp/feed', 'https://user:pass@example.com/feed',
                     'https://@example.com/feed', 'https://example.com/feed#', 'https://exa mple.com/feed']:
            with self.subTest(feed=feed), self.assertRaises(ValueError):
                config.validate(feed, key)
        with self.assertRaises(ValueError):
            config.validate('https://example.com/feed', base64.b64encode(b'x' * 31).decode())

    def test_updates_both_files_and_can_disable_without_changing_version(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Sources').mkdir()
            (root / 'project.yml').write_text('version: 0.4.2\n        SUFeedURL: ""\n        SUPublicEDKey: ""\n')
            info = root / 'Sources/Info.plist'
            info.write_bytes(plistlib.dumps({'CFBundleVersion': '9', 'SUFeedURL': '', 'SUPublicEDKey': ''}))
            feed, key = config.github_feed('WaterBotol/Lunalyr'), base64.b64encode(b'x' * 32).decode()
            config.configure(root, feed, key)
            config.configure(root, '', '', check=True)
            self.assertEqual(plistlib.loads(info.read_bytes())['SUFeedURL'], feed)
            self.assertEqual(plistlib.loads(info.read_bytes())['CFBundleVersion'], '9')
            config.configure(root, '', '')
            config.configure(root, '', '', check=True)
            self.assertEqual(plistlib.loads(info.read_bytes())['SUPublicEDKey'], '')

    def test_invalid_key_does_not_mutate_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Sources').mkdir()
            project = root / 'project.yml'
            project.write_text('        SUFeedURL: ""\n        SUPublicEDKey: ""\n')
            info = root / 'Sources/Info.plist'
            info.write_bytes(plistlib.dumps({'SUFeedURL': '', 'SUPublicEDKey': ''}))
            previous = project.read_bytes(), info.read_bytes()
            with self.assertRaises(ValueError):
                config.configure(root, 'https://example.com/feed', 'wrong')
            self.assertEqual((project.read_bytes(), info.read_bytes()), previous)

    def test_missing_project_marker_does_not_partially_update(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Sources').mkdir()
            project = root / 'project.yml'
            project.write_text('        SUFeedURL: ""\n')
            info = root / 'Sources/Info.plist'
            info.write_bytes(plistlib.dumps({'SUFeedURL': '', 'SUPublicEDKey': ''}))
            previous = project.read_bytes(), info.read_bytes()
            with self.assertRaises(ValueError):
                config.configure(root, 'https://example.com/feed', base64.b64encode(b'x' * 32).decode())
            self.assertEqual((project.read_bytes(), info.read_bytes()), previous)


class AppcastTests(unittest.TestCase):
    def feed(self, root, signature=None, length='4', url=None):
        archive = root / 'VersoCustom-0.4.2.zip'
        archive.write_bytes(b'app!')
        signature = signature if signature is not None else base64.b64encode(b'x' * 64).decode()
        url = url or 'https://github.com/WaterBotol/Lunalyr/releases/download/v0.4.2/VersoCustom-0.4.2.zip'
        feed = root / 'appcast.xml'
        feed.write_text(f'<rss xmlns:sparkle="{appcast.NS}"><channel><item><sparkle:version>9</sparkle:version>'
                        f'<enclosure url="{url}" length="{length}" sparkle:edSignature="{signature}"/>'
                        '</item></channel></rss>')
        return feed, archive

    def test_accepts_matching_packaging_without_claiming_signature_authenticity(self):
        with tempfile.TemporaryDirectory() as directory:
            feed, archive = self.feed(Path(directory))
            appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2')

    def test_rejects_unsigned_or_wrong_length_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for kwargs in [{'signature': ''}, {'length': '100'}]:
                feed, archive = self.feed(root, **kwargs)
                with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                    appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2')

    def test_rejects_wrong_repository_or_tag(self):
        with tempfile.TemporaryDirectory() as directory:
            feed, archive = self.feed(Path(directory))
            for repo, version in [('another/repo', '0.4.2'), ('WaterBotol/Lunalyr', '0.4.3')]:
                with self.subTest(repo=repo, version=version), self.assertRaises(ValueError):
                    appcast.verify(feed, archive, repo, version)


if __name__ == '__main__':
    unittest.main()
