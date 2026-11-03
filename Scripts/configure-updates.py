#!/usr/bin/env python3
"""Configure the public update feed/key in both XcodeGen and Info.plist."""
import argparse
import base64
import json
import plistlib
import re
from pathlib import Path
from urllib.parse import urlsplit


def validate(feed, key):
    feed, key = feed.strip(), key.strip()
    if not feed and not key:
        return feed, key
    parsed = urlsplit(feed)
    if (parsed.scheme.lower() != 'https' or not parsed.hostname or parsed.username is not None
            or parsed.password is not None or '#' in feed or any(c.isspace() for c in feed)):
        raise ValueError('The feed must be an absolute HTTPS URL without credentials or a fragment.')
    try:
        decoded = base64.b64decode(key, validate=True)
    except (ValueError, base64.binascii.Error) as error:
        raise ValueError('Use the public Ed25519 key printed by Sparkle generate_keys.') from error
    if len(decoded) != 32 or base64.b64encode(decoded).decode() != key:
        raise ValueError('The public Ed25519 key must encode exactly 32 bytes.')
    return feed, key


def github_feed(repository):
    repository = repository.strip().removeprefix('https://github.com/').strip('/').removesuffix('.git')
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Use OWNER/REPOSITORY or its https://github.com/OWNER/REPOSITORY URL.')
    return f'https://github.com/{repository}/releases/latest/download/appcast.xml'


def configure(root, feed, key, check=False):
    project, info_path = root / 'project.yml', root / 'Sources/Info.plist'
    old_project, old_info = project.read_bytes(), info_path.read_bytes()
    text, info = old_project.decode(), plistlib.loads(old_info)
    if check:
        values = {}
        for name in ('SUFeedURL', 'SUPublicEDKey'):
            found = re.findall(r'^        ' + name + r': (.*)$', text, re.M)
            if len(found) != 1:
                raise ValueError(f'Expected exactly one {name} in project.yml.')
            values[name] = json.loads(found[0])
            if values[name] != info.get(name, ''):
                raise ValueError(f'{name} differs between project.yml and Info.plist.')
        validate(values['SUFeedURL'], values['SUPublicEDKey'])
        return
    feed, key = validate(feed, key)
    for name, value in (('SUFeedURL', feed), ('SUPublicEDKey', key)):
        text, count = re.subn(r'^        ' + name + r': .*$',
                            lambda _: '        ' + name + ': ' + json.dumps(value), text, flags=re.M)
        if count != 1:
            raise ValueError(f'Expected exactly one {name} in project.yml; nothing was changed.')
        info[name] = value
    new_info = plistlib.dumps(info, sort_keys=False)
    try:
        project.write_text(text)
        info_path.write_bytes(new_info)
    except OSError:
        project.write_bytes(old_project)
        info_path.write_bytes(old_info)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    destination = parser.add_mutually_exclusive_group()
    destination.add_argument('--github', help='OWNER/REPO or GitHub repository URL')
    destination.add_argument('--feed-url', help='An alternative HTTPS appcast URL')
    destination.add_argument('--disable', action='store_true')
    destination.add_argument('--check', action='store_true')
    parser.add_argument('--public-key', default='', help='Public key only, never the private signing key')
    args = parser.parse_args()
    try:
        if not (args.check or args.disable or args.github or args.feed_url):
            parser.error('Choose --github, --feed-url, --disable or --check.')
        feed = github_feed(args.github) if args.github else args.feed_url or ''
        key = '' if args.disable else args.public_key
        configure(Path(__file__).resolve().parent.parent, feed, key, check=args.check)
        print('Update configuration checked.' if args.check else 'Update configuration saved. Rebuild the app next.')
    except (OSError, ValueError) as error:
        parser.exit(1, f'Update setup: {error}\n')


if __name__ == '__main__':
    main()
