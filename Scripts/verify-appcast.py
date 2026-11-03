#!/usr/bin/env python3
"""Check the generated release feed before it is attached to a GitHub release.

This checks packaging consistency; Sparkle performs cryptographic verification.
"""
import base64
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = 'http://www.andymatuschak.org/xml-namespaces/sparkle'


def verify(feed, archive, repository, version):
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
            raise ValueError('The update archive has no valid-format Ed25519 signature.')
        if enclosure.get('length') != str(archive.stat().st_size):
            raise ValueError('The update size does not match the archive.')
        build = item.findtext(f'{{{NS}}}version') or enclosure.get(f'{{{NS}}}version')
        if not build:
            raise ValueError('The update has no Sparkle build version.')
        return
    raise ValueError('No release enclosure matches the expected GitHub URL.')


if __name__ == '__main__':
    try:
        verify(Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3], sys.argv[4])
        print('Appcast packaging checked. Cryptographic verification is performed by Sparkle.')
    except (IndexError, OSError, ValueError, ET.ParseError) as error:
        raise SystemExit(f'Appcast: {error}')
