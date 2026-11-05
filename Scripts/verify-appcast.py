#!/usr/bin/env python3
"""Check a release feed before it is attached to a GitHub release.

Besides packaging consistency, this verifies the archive's Ed25519 signature
against the SUPublicEDKey embedded in the app being released (with CryptoKit,
through Tools/verify-update-signature.swift) and requires the build number to
exceed every build in the live feed. It then compares the app with the live
release, which is what installed copies run: the update key must be the one
they embed, and once that release has a stable code-signing identity, the app
must satisfy its designated requirement, which macOS checks before it keeps a
System Audio / Screen Recording permission. A mismatched or rotated signing
secret, a forgotten CFBundleVersion bump or a missing signing certificate
therefore fails here, instead of producing an update that installed copies
reject as improperly signed, ignore as not newer, or install while macOS
silently withdraws their permissions.
"""
import argparse
import base64
import os
import plistlib
import re
import subprocess
import tempfile
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path, PurePosixPath

NS = 'http://www.andymatuschak.org/xml-namespaces/sparkle'
SIGNATURE_TOOL = Path(__file__).resolve().parent.parent / 'Tools' / 'verify-update-signature.swift'
# Far above any Lunalyr release; it only stops a wrong URL from filling the disk.
MAX_ARCHIVE_BYTES = 1 << 30
FIRST_UPDATE = ('Only for the very first update a repository publishes, pass --previous-feed none '
                '(LUNALYR_PREVIOUS_APPCAST=none, or tick first_update when preparing the release in GitHub Actions).')
OFFLINE = 'Pass a copy of the live release with --previous-app (LUNALYR_PREVIOUS_APP).'


def build_number(value):
    """A CFBundleVersion as a comparable tuple; Apple allows one to three integers."""
    text = (value or '').strip()
    if not re.fullmatch(r'\d+(\.\d+){0,2}', text):
        raise ValueError(f'Build number {value!r} must be one to three period-separated integers.')
    return tuple(int(part) for part in text.split('.'))


def item_build(item):
    enclosure = item.find('enclosure')
    return item.findtext(f'{{{NS}}}version') or (
        enclosure.get(f'{{{NS}}}version') if enclosure is not None else None)


def newest_item(feed_bytes):
    """(build, enclosure URL) of the highest build a feed offers, or None when it lists none."""
    offers = []
    for item in ET.fromstring(feed_bytes).findall('./channel/item'):
        build = item_build(item)
        if build:
            enclosure = item.find('enclosure')
            offers.append((build, enclosure.get('url') if enclosure is not None else None))
    return max(offers, key=lambda offer: build_number(offer[0])) if offers else None


def newest_build(feed_bytes):
    """The highest build any item in a feed offers, or None when it lists none."""
    newest = newest_item(feed_bytes)
    return newest[0] if newest else None


def read_feed(source):
    """Feed bytes from an HTTPS URL or a saved file; None only for an explicit "none"."""
    if source == 'none':
        return None
    if '://' not in source:
        return Path(source).read_bytes()
    if not source.startswith('https://'):
        raise ValueError('The live feed must be an HTTPS URL.')
    request = urllib.request.Request(source, headers={'User-Agent': 'Lunalyr release check'})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        if error.fp is not None:
            error.close()
        if error.code == 404:
            # The latest release has no appcast.xml: either nothing has been
            # published yet or a release went out without its feed. Only the
            # owner can tell, so neither case skips the comparison silently.
            raise ValueError(f'The live feed {source} does not exist (HTTP 404), so the latest release has no '
                             f'appcast.xml. Attach it to that release or pass a saved copy. {FIRST_UPDATE}') from error
        raise ValueError(f'Could not read the live feed {source}: HTTP {error.code}.') from error
    except (urllib.error.URLError, OSError) as error:
        raise ValueError(f'Could not read the live feed {source} ({error}). Pass a saved copy instead.') from error


def download(url, destination):
    """Save the live release archive over HTTPS, refusing an implausible size."""
    if not (url or '').startswith('https://'):
        raise ValueError(f'The live feed offers its update at {url!r}, which is not an HTTPS URL. {OFFLINE}')
    request = urllib.request.Request(url, headers={'User-Agent': 'Lunalyr release check'})
    try:
        with urllib.request.urlopen(request, timeout=60) as response, open(destination, 'wb') as output:
            if not response.geturl().startswith('https://'):
                raise ValueError(f'The download of {url} was redirected away from HTTPS. {OFFLINE}')
            received = 0
            while True:
                chunk = response.read(1 << 20)
                if not chunk:
                    break
                received += len(chunk)
                if received > MAX_ARCHIVE_BYTES:
                    raise ValueError(f'The live release archive at {url} is implausibly large. {OFFLINE}')
                output.write(chunk)
    except urllib.error.HTTPError as error:
        if error.fp is not None:
            error.close()
        raise ValueError(f'Could not download the live release {url}: HTTP {error.code}. {OFFLINE}') from error
    except (urllib.error.URLError, OSError) as error:
        raise ValueError(f'Could not download the live release {url} ({error}). {OFFLINE}') from error


def unpack_app(archive, directory):
    """The one app in a release ZIP, wherever it sits, unpacked with ditto so its code signature survives."""
    try:
        with zipfile.ZipFile(archive) as bundle:
            names = bundle.namelist()
    except zipfile.BadZipFile as error:
        raise ValueError(f'{archive} is not a ZIP archive.') from error
    for name in names:
        if name.startswith('/') or '..' in PurePosixPath(name).parts:
            raise ValueError(f'{archive} contains an unsafe path: {name}')
    directory.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(['ditto', '-x', '-k', str(archive), str(directory)],
                            capture_output=True, text=True, timeout=600)
    if result.returncode != 0:
        raise ValueError(f'Could not unpack {archive}: {result.stderr.strip()}')
    apps = []
    for folder, subfolders, _ in os.walk(directory):
        apps += [Path(folder) / name for name in subfolders if name.endswith('.app')]
        subfolders[:] = [name for name in subfolders if not name.endswith('.app') and name != '__MACOSX']
    if len(apps) != 1:
        raise ValueError(f'{archive} should contain exactly one app, not {len(apps)}. {OFFLINE}')
    return apps[0]


def live_app(source, feed_bytes, directory):
    """The app installed copies run: a given copy (an .app or its ZIP), else the live feed's newest download."""
    if source is not None:
        source = Path(source)
        return source if source.is_dir() else unpack_app(source, directory / 'live')
    archive = directory / 'live.zip'
    download(newest_item(feed_bytes)[1], archive)
    return unpack_app(archive, directory / 'live')


def info_plist(app):
    with open(Path(app) / 'Contents' / 'Info.plist', 'rb') as handle:
        return plistlib.load(handle)


def code_identity(app):
    """(signed ad hoc or not at all, designated requirement) as codesign reports them."""
    result = subprocess.run(['codesign', '--display', '--verbose=2', '-r-', str(app)],
                            capture_output=True, text=True, timeout=120)
    if result.returncode != 0:
        if 'not signed at all' in result.stderr:
            return True, None
        raise ValueError(f'Could not read the code signature of {app}: {result.stderr.strip()}')
    # Ad hoc code only has an implicit requirement, which codesign prints as a comment.
    requirement = re.search(r'^#?\s*designated => (.+)$', result.stdout, re.M)
    adhoc = re.search(r'^Signature=adhoc$', result.stderr, re.M) is not None
    return adhoc, requirement.group(1).strip() if requirement else None


def satisfies_requirement(app, requirement):
    """Whether the app is the code a requirement describes, the test macOS applies to stored permissions."""
    result = subprocess.run(['codesign', '--verify', '-R', '=' + requirement, str(app)],
                            capture_output=True, text=True, timeout=120)
    return result.returncode == 0


def check_continuity(app, live, allow_key_change=False, allow_identity_change=False,
                     identity=code_identity, satisfies=satisfies_requirement):
    """Notes on updating from the live release to the app; raises where installed copies would break."""
    notes = []
    key = str(info_plist(app).get('SUPublicEDKey', '')).strip()
    live_key = str(info_plist(live).get('SUPublicEDKey', '')).strip()
    if live_key and key != live_key:
        if not allow_key_change:
            raise ValueError(f'This app embeds update key {key}, but the live release embeds {live_key}. '
                             'Installed copies verify every update with the key they embed, so they would reject '
                             'this one as improperly signed. Use the original SPARKLE_PUBLIC_KEY and '
                             'SPARKLE_PRIVATE_KEY pair again. Only if installed copies are meant to stop updating, '
                             'pass --allow-key-change (LUNALYR_ALLOW_KEY_ROTATION=1).')
        notes.append('Warning: the update key changes, so installed copies cannot install this update.')
    live_adhoc, live_requirement = identity(live)
    adhoc, requirement = identity(app)
    signed = 'This app is signed ad hoc' if adhoc else f'This app is signed as: {requirement}'
    if live_adhoc or not live_requirement:
        notes.append(f'{signed}. The live release is signed ad hoc, so its users grant System Audio / Screen '
                     'Recording again after this update' + ('.' if adhoc else '; later updates must keep this '
                                                             'identity to keep those permissions.'))
    elif satisfies(app, live_requirement):
        notes.append(f'{signed}, which satisfies the live release\'s identity, so macOS keeps users\' '
                     'System Audio / Screen Recording permissions.')
    elif allow_identity_change:
        notes.append(f'Warning: {signed}, not as the live release ({live_requirement}), so every user must '
                     'grant System Audio / Screen Recording again.')
    else:
        raise ValueError(f'The live release is signed as {live_requirement}, but {signed[0].lower()}{signed[1:]}. '
                         'macOS would silently withdraw every user\'s System Audio / Screen Recording permission '
                         'after this update. Sign it with the same certificate (the release job needs the '
                         'MACOS_CODESIGN_* secrets), or accept the change with --allow-identity-change '
                         '(LUNALYR_ALLOW_IDENTITY_CHANGE=1, or tick new_signing_identity when preparing the '
                         'release in GitHub Actions).')
    return notes


def run_signature_tool(archive, signature, public_key):
    """Raise unless the CryptoKit helper accepts the signature for this archive and key."""
    command = ['xcrun', 'swift', str(SIGNATURE_TOOL), str(archive), signature, public_key]
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=600)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ValueError(f'The signature check could not run ({error}); it needs Xcode or its '
                         'command-line tools.') from error
    if result.returncode != 0:
        raise ValueError((result.stderr or result.stdout).strip() or 'The update signature did not verify.')


def verify_against_app(item, enclosure, archive, version, build, signature, app, check_signature):
    """The feed must describe this exact app, signed with the key it carries."""
    info = info_plist(app)
    key = str(info.get('SUPublicEDKey', '')).strip()
    try:
        valid_key = len(base64.b64decode(key, validate=True)) == 32
    except (ValueError, base64.binascii.Error):
        valid_key = False
    if not valid_key:
        raise ValueError('The app has no valid SUPublicEDKey, so installed copies could not verify its updates.')
    if info.get('CFBundleShortVersionString') != version:
        raise ValueError(f'The app is version {info.get("CFBundleShortVersionString")}, not {version}.')
    if str(info.get('CFBundleVersion', '')) != build:
        raise ValueError(f'The feed offers build {build}, but the app is build {info.get("CFBundleVersion")}.')
    short = item.findtext(f'{{{NS}}}shortVersionString') or enclosure.get(f'{{{NS}}}shortVersionString')
    if short and short != version:
        raise ValueError(f'The feed calls this update {short}, not {version}.')
    check_signature(archive, signature, key)


def verify(feed, archive, repository, version, app=None, previous_feed=None, check_signature=run_signature_tool):
    expected = f'https://github.com/{repository}/releases/download/v{version}/{archive.name}'
    items = ET.parse(feed).getroot().findall('./channel/item')
    for item in items:
        enclosure = item.find('enclosure')
        if enclosure is None or enclosure.get('url') != expected:
            continue
        signature = enclosure.get(f'{{{NS}}}edSignature', '')
        try:
            valid_signature = len(base64.b64decode(signature, validate=True)) == 64
        except (ValueError, base64.binascii.Error):
            valid_signature = False
        if not valid_signature:
            raise ValueError('The update archive has no valid-format Ed25519 signature. generate_appcast leaves '
                             'it out when the private key does not match the app\'s SUPublicEDKey.')
        if enclosure.get('length') != str(archive.stat().st_size):
            raise ValueError('The update size does not match the archive.')
        build = item_build(item)
        if not build:
            raise ValueError('The update has no Sparkle build version.')
        build_number(build)
        if app is not None:
            verify_against_app(item, enclosure, archive, version, build, signature, app, check_signature)
        if previous_feed is not None:
            newest = newest_build(previous_feed)
            if newest is not None and build_number(build) <= build_number(newest):
                raise ValueError(f'Build {build} is not newer than build {newest} in the live feed, so installed '
                                 'copies would report that they are up to date. Raise CFBundleVersion in project.yml.')
        return
    raise ValueError('No release enclosure matches the expected GitHub URL.')


def check_release(feed, archive, repository, version, app, previous_feed, previous_app=None,
                  allow_key_change=False, allow_identity_change=False, check_signature=run_signature_tool,
                  identity=code_identity, satisfies=satisfies_requirement):
    """Every check the command line runs; returns notes to print and raises ValueError on a failure."""
    live_feed = read_feed(previous_feed)
    if live_feed is not None and newest_item(live_feed) is None:
        raise ValueError(f'The live feed lists no update to compare this one with. {FIRST_UPDATE}')
    verify(feed, archive, repository, version, app=app, previous_feed=live_feed, check_signature=check_signature)
    if live_feed is None and previous_app is None:
        return ['First update: there is no live release to compare the build, update key and code identity with.']
    with tempfile.TemporaryDirectory(prefix='lunalyr-live-release-') as directory:
        live = live_app(previous_app, live_feed, Path(directory))
        return check_continuity(app, live, allow_key_change, allow_identity_change, identity, satisfies)


def main():
    parser = argparse.ArgumentParser(description='Check a generated appcast before it is published.')
    parser.add_argument('feed', type=Path, help='The new appcast.xml')
    parser.add_argument('archive', type=Path, help='The update ZIP the appcast describes')
    parser.add_argument('repository', help='OWNER/REPO of the GitHub release')
    parser.add_argument('version', help='The marketing version, as in the v<version> tag')
    parser.add_argument('--app', type=Path, required=True,
                        help='The Lunalyr.app that was archived; supplies the public key and build')
    parser.add_argument('--previous-feed', required=True,
                        help='The live appcast URL or a saved copy; "none" only for the very first update')
    parser.add_argument('--previous-app', type=Path,
                        help="A copy of the live release's app or ZIP; by default the live feed's newest is downloaded")
    parser.add_argument('--allow-key-change', action='store_true',
                        help='Accept an update key other than the live release\'s; installed copies cannot install it')
    parser.add_argument('--allow-identity-change', action='store_true',
                        help='Accept a signing identity other than the live release\'s; users grant permissions again')
    args = parser.parse_args()
    try:
        for note in check_release(args.feed, args.archive, args.repository, args.version, args.app,
                                  args.previous_feed, args.previous_app, args.allow_key_change,
                                  args.allow_identity_change):
            print(note)
        print('Appcast checked: packaging, the signature against the app\'s key, the build number, and the update '
              'key and code identity installed copies expect.')
    except (OSError, ValueError, ET.ParseError, subprocess.SubprocessError) as error:
        raise SystemExit(f'Appcast: {error}')


if __name__ == '__main__':
    main()
