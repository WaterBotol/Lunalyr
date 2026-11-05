import base64
import importlib.util
import io
import json
import os
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import urllib.response
import zipfile
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
MAC_TOOLS = sys.platform == 'darwin' and all(shutil.which(tool) for tool in ('xcrun', 'codesign', 'ditto'))


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


KEY = base64.b64encode(b'k' * 32).decode()
OTHER_KEY = base64.b64encode(b'o' * 32).decode()
RELEASE_URL = 'https://github.com/WaterBotol/Lunalyr/releases/download/v0.4.2/VersoCustom-0.4.2.zip'
LIVE_FEED = 'https://github.com/WaterBotol/Lunalyr/releases/latest/download/appcast.xml'
LIVE_ARCHIVE = 'https://github.com/WaterBotol/Lunalyr/releases/download/v0.4.1/Lunalyr-0.4.1.zip'


def make_app(root, key=KEY, build='9', version='0.4.2'):
    contents = root / 'Lunalyr.app' / 'Contents'
    contents.mkdir(parents=True, exist_ok=True)
    (contents / 'Info.plist').write_bytes(plistlib.dumps(
        {'CFBundleShortVersionString': version, 'CFBundleVersion': build, 'SUPublicEDKey': key}))
    return root / 'Lunalyr.app'


def write_feed(path, signature, length, build='9', url=RELEASE_URL, short=None):
    short_element = f'<sparkle:shortVersionString>{short}</sparkle:shortVersionString>' if short else ''
    path.write_text(f'<rss xmlns:sparkle="{appcast.NS}"><channel><item><sparkle:version>{build}</sparkle:version>'
                    f'{short_element}<enclosure url="{url}" length="{length}" sparkle:edSignature="{signature}"/>'
                    '</item></channel></rss>')
    return path


def accept_signature(*arguments):
    """Stands in for the CryptoKit check where only the feed logic is under test."""


def live_feed(build='8', url=LIVE_ARCHIVE):
    """The feed installed copies read, offering the release they run."""
    return (f'<rss xmlns:sparkle="{appcast.NS}"><channel><item><sparkle:version>{build}</sparkle:version>'
            f'<enclosure url="{url}" length="1" sparkle:edSignature="x"/></item></channel></rss>').encode()


def serve(responses, requests=None):
    """Stands in for urlopen: fixed bytes for each known URL, a network failure for any other."""
    def urlopen(request, timeout):
        if requests is not None:
            requests.append(request.full_url)
        if request.full_url not in responses:
            raise appcast.urllib.error.URLError(f'unexpected request for {request.full_url}')
        return urllib.response.addinfourl(io.BytesIO(responses[request.full_url]), {}, request.full_url, 200)
    return urlopen


def identities(**table):
    """Stands in for code_identity, by the name of the folder holding each app."""
    return lambda app: table[Path(app).parent.name]


def compile_executable(directory):
    """A tiny Mach-O program, so fixture apps can carry real code signatures."""
    executable = Path(directory) / 'fixture'
    subprocess.run(['xcrun', 'clang', '-x', 'c', '-', '-o', str(executable)], input=b'int main(void) { return 0; }\n',
                   check=True, capture_output=True, timeout=300)
    return executable


def signed_app(root, executable, key=KEY, build='9', version='0.4.2'):
    """An ad hoc signed Lunalyr.app; builds that differ only in Info.plist still get different code hashes."""
    app = root / 'Lunalyr.app'
    (app / 'Contents' / 'MacOS').mkdir(parents=True)
    shutil.copy2(executable, app / 'Contents' / 'MacOS' / 'Lunalyr')
    (app / 'Contents' / 'Info.plist').write_bytes(plistlib.dumps({
        'CFBundleExecutable': 'Lunalyr', 'CFBundleIdentifier': 'local.verso.custom', 'CFBundlePackageType': 'APPL',
        'CFBundleShortVersionString': version, 'CFBundleVersion': build, 'SUFeedURL': LIVE_FEED,
        'SUPublicEDKey': key}))
    subprocess.run(['codesign', '--force', '--sign', '-', str(app)], check=True, capture_output=True, timeout=120)
    return app


class AppcastTests(unittest.TestCase):
    def feed(self, root, signature=None, length='4', url=None, build='9', short=None):
        archive = root / 'VersoCustom-0.4.2.zip'
        archive.write_bytes(b'app!')
        signature = signature if signature is not None else base64.b64encode(b'x' * 64).decode()
        feed = write_feed(root / 'appcast.xml', signature, length, build, url or RELEASE_URL, short)
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

    def test_signature_is_checked_against_the_archived_apps_key(self):
        calls = []
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            feed, archive = self.feed(root, short='0.4.2')
            appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', app=make_app(root),
                           check_signature=lambda *arguments: calls.append(arguments))
        self.assertEqual(calls, [(archive, base64.b64encode(b'x' * 64).decode(), KEY)])

    def test_feed_must_describe_the_archived_app(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            feed, archive = self.feed(root)
            for app in [{'build': '10'}, {'version': '0.4.3'}, {'key': ''},
                        {'key': base64.b64encode(b'k' * 31).decode()}, {'key': 'not base64!'}]:
                with self.subTest(app=app), self.assertRaises(ValueError):
                    appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', app=make_app(root, **app),
                                   check_signature=accept_signature)
            feed, archive = self.feed(root, short='0.4.3')
            with self.assertRaises(ValueError):
                appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', app=make_app(root),
                               check_signature=accept_signature)

    def test_build_must_be_newer_than_every_build_in_the_live_feed(self):
        live = (f'<rss xmlns:sparkle="{appcast.NS}"><channel>'
                '<item><sparkle:version>8</sparkle:version></item>'
                '<item><enclosure url="https://example.com/old.zip" sparkle:version="10"/></item>'
                '</channel></rss>').encode()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for build, newer in [('9', False), ('10', False), ('11', True), ('10.1', True)]:
                feed, archive = self.feed(root, build=build)
                with self.subTest(build=build):
                    if newer:
                        appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', previous_feed=live)
                    else:
                        with self.assertRaisesRegex(ValueError, 'not newer'):
                            appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', previous_feed=live)
            # Nothing published yet: the first update is not blocked.
            feed, archive = self.feed(root, build='1')
            empty = f'<rss xmlns:sparkle="{appcast.NS}"><channel/></rss>'.encode()
            appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', previous_feed=empty)
            appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', previous_feed=None)

    def test_build_numbers_compare_as_integers(self):
        self.assertLess(appcast.build_number('9'), appcast.build_number('10'))
        self.assertLess(appcast.build_number('1.9'), appcast.build_number('1.10'))
        self.assertLess(appcast.build_number('17'), appcast.build_number('17.1'))
        for value in ['', 'abc', '17b', '1.2.3.4', '-1', None]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                appcast.build_number(value)

    def test_live_feed_is_read_without_guessing(self):
        with tempfile.TemporaryDirectory() as directory:
            saved = Path(directory) / 'appcast.xml'
            saved.write_bytes(b'<rss/>')
            self.assertEqual(appcast.read_feed(str(saved)), b'<rss/>')
        self.assertIsNone(appcast.read_feed('none'))
        with self.assertRaises(ValueError):
            appcast.read_feed('http://example.com/appcast.xml')

        def status(code):
            def urlopen(request, timeout):
                raise appcast.urllib.error.HTTPError(request.full_url, code, 'status', {}, io.BytesIO())
            return urlopen

        def offline(request, timeout):
            raise appcast.urllib.error.URLError('offline')

        # A missing feed is not taken to mean "first update": the latest
        # release may just lack its appcast, so only an explicit none skips.
        with mock.patch.object(appcast.urllib.request, 'urlopen', status(404)), \
                self.assertRaisesRegex(ValueError, 'HTTP 404.*--previous-feed none'):
            appcast.read_feed(LIVE_FEED)
        for failure in [status(500), offline]:
            with self.subTest(failure=failure), mock.patch.object(appcast.urllib.request, 'urlopen', failure), \
                    self.assertRaises(ValueError):
                appcast.read_feed(LIVE_FEED)

    def test_command_line_requires_the_app_and_the_live_feed(self):
        result = subprocess.run([sys.executable, '-I', str(ROOT / 'Scripts' / 'verify-appcast.py'),
                                 'appcast.xml', 'update.zip', 'WaterBotol/Lunalyr', '0.4.2'],
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 2)
        self.assertIn('--app', result.stderr)


class ContinuityTests(unittest.TestCase):
    """What installed copies of the live release make of the new app, with code signatures stubbed."""

    STABLE = (False, 'identifier "local.verso.custom" and certificate root = H"0123"')
    OTHER_CERTIFICATE = (False, 'identifier "local.verso.custom" and certificate root = H"4567"')
    AD_HOC = (True, 'cdhash H"89ab"')

    def apps(self, root, key=KEY, live_key=KEY):
        return make_app(root / 'new', key, build='9'), make_app(root / 'live', live_key, build='8', version='0.4.1')

    def test_update_key_must_be_the_one_installed_copies_hold(self):
        same = identities(new=self.AD_HOC, live=self.AD_HOC)
        with tempfile.TemporaryDirectory() as directory:
            # Both secrets replaced by a fresh pair: the archive verifies with
            # the new app's own key, but installed copies hold the old one.
            app, live = self.apps(Path(directory), key=OTHER_KEY)
            with self.assertRaisesRegex(ValueError, 'improperly signed'):
                appcast.check_continuity(app, live, identity=same)
            notes = appcast.check_continuity(app, live, allow_key_change=True, identity=same)
            self.assertIn('Warning: the update key changes', notes[0])
            appcast.check_continuity(*self.apps(Path(directory)), identity=same)
            # A live release without updates configured holds no key to keep.
            appcast.check_continuity(*self.apps(Path(directory), key=OTHER_KEY, live_key=''), identity=same)

    def test_stable_identity_must_carry_over(self):
        checked = []

        def fails(app, requirement):
            checked.append((app, requirement))
            return False

        with tempfile.TemporaryDirectory() as directory:
            app, live = self.apps(Path(directory))
            # The certificate secret did not reach the job, or was replaced.
            for new, message in ((self.AD_HOC, 'but this app is signed ad hoc'),
                                 (self.OTHER_CERTIFICATE, 'but this app is signed as: .*H"4567"')):
                observed = identities(new=new, live=self.STABLE)
                with self.subTest(new=new):
                    with self.assertRaisesRegex(ValueError, message + '.*System Audio / Screen Recording'):
                        appcast.check_continuity(app, live, identity=observed, satisfies=fails)
                    notes = appcast.check_continuity(app, live, allow_identity_change=True, identity=observed,
                                                     satisfies=fails)
                    self.assertIn('Warning', notes[0])
            self.assertEqual(checked[0], (app, self.STABLE[1]))
            notes = appcast.check_continuity(app, live, identity=identities(new=self.STABLE, live=self.STABLE),
                                             satisfies=lambda *arguments: True)
            self.assertIn('macOS keeps', notes[0])

    def test_an_ad_hoc_live_release_has_no_identity_to_keep(self):
        def unused(*arguments):
            raise AssertionError('an ad hoc requirement names a single build, so nothing can carry it over')

        with tempfile.TemporaryDirectory() as directory:
            app, live = self.apps(Path(directory))
            for new, expected in ((self.AD_HOC, 'grant System Audio / Screen Recording again after this update.'),
                                  (self.STABLE, 'later updates must keep this identity')):
                with self.subTest(new=new):
                    notes = appcast.check_continuity(app, live, identity=identities(new=new, live=self.AD_HOC),
                                                     satisfies=unused)
                    self.assertIn(expected, notes[0])


class ReleaseCheckTests(unittest.TestCase):
    """The whole command-line check, with the live release served from memory."""

    def check(self, root, previous_feed, **options):
        archive = root / 'VersoCustom-0.4.2.zip'
        archive.write_bytes(b'app!')
        feed = write_feed(root / 'appcast.xml', base64.b64encode(b'x' * 64).decode(), '4')
        return appcast.check_release(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', make_app(root / 'new'),
                                     previous_feed, check_signature=accept_signature, **options)

    def test_only_an_explicit_none_skips_the_live_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root, requests = Path(directory), []
            with mock.patch.object(appcast.urllib.request, 'urlopen', serve({}, requests)):
                notes = self.check(root, 'none')
            self.assertEqual(requests, [])
            self.assertIn('First update', notes[0])
            empty = root / 'empty.xml'
            empty.write_bytes(f'<rss xmlns:sparkle="{appcast.NS}"><channel/></rss>'.encode())
            with self.assertRaisesRegex(ValueError, 'lists no update.*--previous-feed none'):
                self.check(root, str(empty))

    def test_build_is_compared_before_the_live_release_is_downloaded(self):
        with tempfile.TemporaryDirectory() as directory:
            requests = []
            with mock.patch.object(appcast.urllib.request, 'urlopen', serve({LIVE_FEED: live_feed('9')}, requests)), \
                    self.assertRaisesRegex(ValueError, 'not newer'):
                self.check(Path(directory), LIVE_FEED)
            self.assertEqual(requests, [LIVE_FEED])

    def test_live_release_must_be_downloadable_over_https(self):
        with tempfile.TemporaryDirectory() as directory:
            for feed, message in ((live_feed(url='http://example.com/Lunalyr-0.4.1.zip'), 'not an HTTPS URL'),
                                  (live_feed(), 'Could not download.*--previous-app')):
                with self.subTest(message=message), \
                        mock.patch.object(appcast.urllib.request, 'urlopen', serve({LIVE_FEED: feed})), \
                        self.assertRaisesRegex(ValueError, message):
                    self.check(Path(directory), LIVE_FEED)

    def test_download_refuses_redirects_away_from_https_and_runaway_sizes(self):
        def redirected(request, timeout):
            return urllib.response.addinfourl(io.BytesIO(b'zip'), {}, 'http://example.com/Lunalyr.zip', 200)

        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'live.zip'
            with mock.patch.object(appcast.urllib.request, 'urlopen', redirected), \
                    self.assertRaisesRegex(ValueError, 'away from HTTPS'):
                appcast.download(LIVE_ARCHIVE, target)
            with mock.patch.object(appcast.urllib.request, 'urlopen', serve({LIVE_ARCHIVE: b'x' * 10})):
                with mock.patch.object(appcast, 'MAX_ARCHIVE_BYTES', 4), \
                        self.assertRaisesRegex(ValueError, 'implausibly large'):
                    appcast.download(LIVE_ARCHIVE, target)
                appcast.download(LIVE_ARCHIVE, target)
            self.assertEqual(target.read_bytes(), b'x' * 10)

    def test_unpacking_refuses_archives_that_are_not_safe_release_zips(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for entry in ('../escape.txt', 'Lunalyr.app/../../escape.txt', '/etc/escape.txt'):
                archive = root / 'unsafe.zip'
                with zipfile.ZipFile(archive, 'w') as bundle:
                    bundle.writestr(entry, 'x')
                with self.subTest(entry=entry), self.assertRaisesRegex(ValueError, 'unsafe path'):
                    appcast.unpack_app(archive, root / 'out')
            (root / 'plain.zip').write_bytes(b'not a zip')
            with self.assertRaisesRegex(ValueError, 'not a ZIP'):
                appcast.unpack_app(root / 'plain.zip', root / 'out')
            self.assertFalse((root / 'out').exists())


@unittest.skipUnless(MAC_TOOLS, 'needs codesign, ditto and clang')
class LiveReleaseTests(unittest.TestCase):
    """The comparison with the live release using real signatures and archives; only the network is faked."""

    @classmethod
    def setUpClass(cls):
        cls.workspace = tempfile.TemporaryDirectory()
        cls.executable = compile_executable(cls.workspace.name)

    @classmethod
    def tearDownClass(cls):
        cls.workspace.cleanup()

    def published(self, root, key=KEY):
        """The live release as installed copies run it, and the responses that publish it."""
        live = signed_app(root / 'live', self.executable, key=key, build='8', version='0.4.1')
        archive = root / 'Lunalyr-0.4.1.zip'
        subprocess.run(['ditto', '-c', '-k', '--sequesterRsrc', '--keepParent', str(live), str(archive)],
                       check=True, capture_output=True, timeout=120)
        return live, archive, {LIVE_FEED: live_feed(), LIVE_ARCHIVE: archive.read_bytes()}

    def release(self, root, key=KEY):
        app = signed_app(root / 'new', self.executable, key=key, build='9')
        archive = root / 'VersoCustom-0.4.2.zip'
        archive.write_bytes(b'app!')
        return app, archive, write_feed(root / 'appcast.xml', base64.b64encode(b'x' * 64).decode(), '4')

    def check(self, app, archive, feed, previous_feed=LIVE_FEED, **options):
        return appcast.check_release(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', app, previous_feed,
                                     check_signature=accept_signature, **options)

    def test_downloads_the_live_release_and_accepts_a_matching_update(self):
        with tempfile.TemporaryDirectory() as directory:
            root, requests = Path(directory), []
            responses = self.published(root)[2]
            with mock.patch.object(appcast.urllib.request, 'urlopen', serve(responses, requests)):
                notes = self.check(*self.release(root))
            self.assertEqual(requests, [LIVE_FEED, LIVE_ARCHIVE])
            self.assertIn('This app is signed ad hoc. The live release is signed ad hoc', notes[0])

    def test_rejects_an_update_key_installed_copies_do_not_have(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            responses = self.published(root)[2]
            release = self.release(root, key=OTHER_KEY)
            mismatch = f'embeds update key {re.escape(OTHER_KEY)}.*embeds {re.escape(KEY)}'
            with mock.patch.object(appcast.urllib.request, 'urlopen', serve(responses)):
                with self.assertRaisesRegex(ValueError, mismatch):
                    self.check(*release)
                self.assertIn('Warning', self.check(*release, allow_key_change=True)[0])

    def test_a_copy_of_the_live_release_replaces_the_download(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            live, archive, responses = self.published(root)
            release = self.release(root)
            saved = root / 'live.xml'
            saved.write_bytes(responses[LIVE_FEED])
            for copy in (live, archive):
                with self.subTest(copy=copy.name), mock.patch.object(appcast.urllib.request, 'urlopen', serve({})):
                    self.check(*release, previous_feed=str(saved), previous_app=copy)
            # Without a published feed, a given copy is still compared.
            with self.assertRaisesRegex(ValueError, 'improperly signed'):
                self.check(*self.release(root / 'rotated', key=OTHER_KEY), previous_feed='none', previous_app=live)

    def test_an_ad_hoc_build_cannot_follow_a_certificate_signed_release(self):
        apple = appcast.code_identity('/usr/bin/true')
        self.assertFalse(apple[0])
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            responses = self.published(root)[2]
            app, archive, feed = self.release(root)

            def live_as(identity):
                return lambda path: appcast.code_identity(path) if Path(path) == app else identity

            with mock.patch.object(appcast.urllib.request, 'urlopen', serve(responses)):
                with self.assertRaisesRegex(ValueError, 'this app is signed ad hoc'):
                    self.check(app, archive, feed, identity=live_as(apple))
                notes = self.check(app, archive, feed, identity=live_as(apple), allow_identity_change=True)
                self.assertIn('Warning', notes[0])
                # The real requirement check also lets matching code through.
                notes = self.check(app, archive, feed, identity=live_as((False, 'identifier "local.verso.custom"')))
                self.assertIn('macOS keeps', notes[0])

    def test_reads_identities_the_way_macos_compares_them(self):
        adhoc, requirement = appcast.code_identity('/usr/bin/true')
        self.assertFalse(adhoc)
        self.assertRegex(requirement, r'^identifier "com\.apple\.true" and anchor apple')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = signed_app(root / 'first', self.executable, build='8')
            second = signed_app(root / 'second', self.executable, build='9')
            adhoc, requirement = appcast.code_identity(first)
            self.assertTrue(adhoc)
            self.assertRegex(requirement, r'^cdhash H"[0-9a-f]+"$')
            # Ad hoc code is identified by its hash alone, which every build changes.
            self.assertTrue(appcast.satisfies_requirement(first, requirement))
            self.assertFalse(appcast.satisfies_requirement(second, requirement))
            self.assertTrue(appcast.satisfies_requirement(second, 'identifier "local.verso.custom"'))
            self.assertFalse(appcast.satisfies_requirement(second, 'identifier "local.verso.custom.debug"'))
            unsigned = make_app(root / 'unsigned')
            self.assertEqual(appcast.code_identity(unsigned), (True, None))
            # Anything else codesign cannot read stops the check instead of passing as unsigned.
            with self.assertRaisesRegex(ValueError, 'Could not read the code signature'):
                appcast.code_identity(root / 'missing.app')

    def test_a_release_archive_must_hold_exactly_one_app(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / 'empty.zip'
            with zipfile.ZipFile(archive, 'w') as bundle:
                bundle.writestr('README.txt', 'no app')
            with self.assertRaisesRegex(ValueError, 'exactly one app, not 0'):
                appcast.unpack_app(archive, root / 'out')
            # A hand-packed archive may keep the app in a folder; it is still found.
            folder = root / 'Lunalyr 0.4.1'
            signed_app(folder, self.executable)
            archive = root / 'folder.zip'
            subprocess.run(['ditto', '-c', '-k', '--sequesterRsrc', '--keepParent', str(folder), str(archive)],
                           check=True, capture_output=True, timeout=120)
            app = appcast.unpack_app(archive, root / 'folder')
            self.assertEqual(app.relative_to(root / 'folder'), Path('Lunalyr 0.4.1') / 'Lunalyr.app')
            self.assertEqual(appcast.code_identity(app)[0], True)
            signed_app(folder / 'second', self.executable)
            archive = root / 'two.zip'
            subprocess.run(['ditto', '-c', '-k', '--keepParent', str(folder), str(archive)],
                           check=True, capture_output=True, timeout=120)
            with self.assertRaisesRegex(ValueError, 'exactly one app, not 2'):
                appcast.unpack_app(archive, root / 'two')


@unittest.skipUnless(MAC_TOOLS and Path('/usr/libexec/PlistBuddy').exists(), 'needs the macOS packaging tools')
class PackageUpdateScriptTests(unittest.TestCase):
    """package-update.sh passes its settings to the release checks and stops when they fail."""

    RECORDER = '\n'.join([
        'import json, os, sys',
        'with open(os.environ["LUNALYR_TEST_RECORD"], "w") as handle:',
        '    json.dump(sys.argv[1:], handle)',
        'sys.exit(int(os.environ.get("LUNALYR_TEST_STATUS", "0")))',
    ])

    @classmethod
    def setUpClass(cls):
        cls.workspace = tempfile.TemporaryDirectory()
        cls.executable = compile_executable(cls.workspace.name)

    @classmethod
    def tearDownClass(cls):
        cls.workspace.cleanup()

    def package(self, **settings):
        """Run the real script in a minimal checkout whose appcast check only records its arguments."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Scripts').mkdir()
            for name in ('package-update.sh', 'configure-updates.py'):
                shutil.copy2(ROOT / 'Scripts' / name, root / 'Scripts' / name)
            (root / 'Scripts' / 'verify-appcast.py').write_text(self.RECORDER)
            (root / 'project.yml').write_text(f'        SUFeedURL: "{LIVE_FEED}"\n        SUPublicEDKey: "{KEY}"\n')
            (root / 'Sources').mkdir()
            (root / 'Sources' / 'Info.plist').write_bytes(plistlib.dumps({'SUFeedURL': LIVE_FEED,
                                                                          'SUPublicEDKey': KEY}))
            tools = root / 'Build' / 'UpdateTools' / 'bin'
            tools.mkdir(parents=True)
            (tools / 'generate_appcast').write_text('#!/bin/sh\nexit 0\n')
            (tools / 'generate_appcast').chmod(0o755)
            signed_app(root / 'Build', self.executable, build='18', version='0.4.11')
            environment = {name: value for name, value in os.environ.items()
                           if not name.startswith(('LUNALYR_', 'SPARKLE_', 'GITHUB_'))}
            environment.update(settings, LUNALYR_TEST_RECORD=str(root / 'checks.json'))
            result = subprocess.run(['bash', str(root / 'Scripts' / 'package-update.sh'), 'WaterBotol/Lunalyr'],
                                    env=environment, capture_output=True, text=True, timeout=300)
            recorded = root / 'checks.json'
            arguments = json.loads(recorded.read_text()) if recorded.exists() else None
            return result, arguments

    def test_checks_receive_the_app_the_live_feed_and_the_overrides(self):
        result, arguments = self.package()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(arguments[2:5], ['WaterBotol/Lunalyr', '0.4.11', '--app'])
        self.assertEqual(Path(arguments[5]).name, 'Lunalyr.app')
        self.assertEqual(arguments[6:], ['--previous-feed', LIVE_FEED])
        result, arguments = self.package(LUNALYR_PREVIOUS_APPCAST='none', LUNALYR_PREVIOUS_APP='/copies/live.zip',
                                         LUNALYR_ALLOW_IDENTITY_CHANGE='1', LUNALYR_ALLOW_KEY_ROTATION='1')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(arguments[6:], ['--previous-feed', 'none', '--previous-app', '/copies/live.zip',
                                         '--allow-identity-change', '--allow-key-change'])
        # Only an exact 1 opts out; an empty value (an unticked workflow box) keeps the live feed.
        result, arguments = self.package(LUNALYR_PREVIOUS_APPCAST='', LUNALYR_ALLOW_IDENTITY_CHANGE='true',
                                         LUNALYR_ALLOW_KEY_ROTATION='yes')
        self.assertEqual(arguments[6:], ['--previous-feed', LIVE_FEED])

    def test_a_failed_check_stops_packaging(self):
        result, arguments = self.package(LUNALYR_TEST_STATUS='1')
        self.assertIsNotNone(arguments)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('Prepared signed update files', result.stdout)


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'needs Swift and CryptoKit')
class UpdateSignatureTests(unittest.TestCase):
    """The real CryptoKit check, with keys created for this test run only."""

    SIGNER = '\n'.join([
        'import CryptoKit',
        'import Foundation',
        'let archive = try Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1]))',
        'let signer = Curve25519.Signing.PrivateKey(), stranger = Curve25519.Signing.PrivateKey()',
        'print(signer.publicKey.rawRepresentation.base64EncodedString())',
        'print(try signer.signature(for: archive).base64EncodedString())',
        'print(stranger.publicKey.rawRepresentation.base64EncodedString())',
    ])

    def test_archive_must_verify_against_the_apps_public_key(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / 'VersoCustom-0.4.2.zip'
            archive.write_bytes(os.urandom(4096))
            script = root / 'sign.swift'
            script.write_text(self.SIGNER)
            signed = subprocess.run(['xcrun', 'swift', str(script), str(archive)], capture_output=True,
                                    text=True, timeout=600, check=True)
            key, signature, stranger = signed.stdout.split()
            feed = write_feed(root / 'appcast.xml', signature, '4096')
            appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', app=make_app(root / 'signer', key))
            with self.assertRaisesRegex(ValueError, 'does not match'):
                appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', app=make_app(root / 'stranger', stranger))
            tampered = bytearray(archive.read_bytes())
            tampered[100] ^= 1
            archive.write_bytes(bytes(tampered))
            with self.assertRaisesRegex(ValueError, 'does not match'):
                appcast.verify(feed, archive, 'WaterBotol/Lunalyr', '0.4.2', app=make_app(root / 'signer', key))


if __name__ == '__main__':
    unittest.main()
