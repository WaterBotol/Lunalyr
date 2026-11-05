"""Files the build scripts and published notices depend on must be in the repository.

The ThirdParty folders were once left untracked, and the documented upload
helper does not add that folder, so a published checkout could neither rebuild
the bundled web engines nor show the sources its notices cite.
"""
import re
import shlex
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE_SCRIPTS = sorted((ROOT / 'Scripts').glob('build-*-engine.sh'))
NOTICES = ROOT / 'THIRD_PARTY_NOTICES.md'


def expand_braces(pattern):
    match = re.search(r'\{([^{}]*)\}', pattern)
    if not match:
        return [pattern]
    head, tail = pattern[:match.start()], pattern[match.end():]
    return [path for option in match.group(1).split(',') for path in expand_braces(head + option + tail)]


def script_references(text):
    """Repository paths (possibly globs) that an engine build script reads or writes."""
    paths = set()
    for match in re.finditer(r'\$project"?/([\w./{},*@-]+)', text):
        paths.update(expand_braces(match.group(1)))
    # The embedded Python receives repository folders as arguments; follow the
    # names it binds them to (`resources / "Name.html"`).
    for call, code in re.findall(r"^python3 - (.*?) <<'PY'\n(.*?)^PY$", text, re.M | re.S):
        arguments = shlex.split(call)
        names = re.search(r'^([\w ,]+)= *map\(Path, sys\.argv\[1:\]\)', code, re.M)
        if not names:
            continue
        for name, argument in zip((name.strip() for name in names.group(1).split(',')), arguments):
            folder = re.fullmatch(r'\$project/([\w./-]+)', argument)
            if not folder:
                continue
            for literal in re.findall(rf'\b{name} */ *f?(?:"([^"]+)"|\'([^\']+)\')', code):
                paths.add(folder.group(1) + '/' + re.sub(r'\{[^}]*\}', '*', literal[0] or literal[1]))
    return paths


def notice_references(text):
    """Repository paths named by the notices: links, paths and bundled licence names."""
    paths = {target.split('#')[0] for target in re.findall(r'\]\(([^)\s]+)\)', text)
             if not re.match(r'[a-z][a-z0-9+.-]*:', target) and not target.startswith('#')}
    paths.update(re.findall(r'(?<![\w/.-])((?:Sources|ThirdParty|Scripts|Tests|Tools)/[\w./-]*[\w-])', text))
    # "bundled in Resources/X" and bare "X-LICENSE.txt" name the app's resources.
    paths.update('Sources/Resources/' + name
                 for name in re.findall(r'(?<![\w/.-])Resources/([\w.-]*\w)', text))
    paths.update('Sources/Resources/' + name
                 for name in re.findall(r'(?<![\w/.-])([\w-]+-LICENSE\.txt)', text))
    return paths


def tracked_files():
    """Paths Git tracks (staged counts), or None outside a Git checkout."""
    if not shutil.which('git'):
        return None
    result = subprocess.run(['git', '-C', str(ROOT), 'ls-files', '-z'], capture_output=True, timeout=60)
    if result.returncode != 0:
        return None
    return {name.decode() for name in result.stdout.split(b'\0') if name}


class ReferencedFilesTests(unittest.TestCase):
    tracked = tracked_files()

    def assert_present(self, source, references):
        for reference in sorted(references):
            with self.subTest(source=source, path=reference):
                matches = [path for path in ROOT.glob(reference)] if '*' in reference else [ROOT / reference]
                self.assertTrue(matches and all(path.exists() for path in matches),
                                f'{source} refers to {reference}, which does not exist')
                if self.tracked is None:
                    continue
                for path in matches:
                    relative = path.relative_to(ROOT).as_posix()
                    present = (relative in self.tracked if path.is_file()
                               else any(name.startswith(relative + '/') for name in self.tracked))
                    self.assertTrue(present, f'{source} refers to {relative}, which Git does not track; '
                                             'add it (git add ThirdParty Sources) so it is published')

    def test_engine_build_scripts_refer_only_to_files_in_the_repository(self):
        self.assertEqual(len(ENGINE_SCRIPTS), 3)
        for script in ENGINE_SCRIPTS:
            references = script_references(script.read_text())
            self.assertGreaterEqual(len(references), 3, f'no repository paths found in {script.name}')
            self.assert_present(script.name, references)

    def test_third_party_notices_refer_only_to_files_in_the_repository(self):
        references = notice_references(NOTICES.read_text())
        self.assertGreaterEqual(len(references), 5, 'no repository paths found in the notices')
        self.assert_present(NOTICES.name, references)

    def test_reference_extraction(self):
        script = ('cp "$project"/ThirdParty/W/lunalyr-{engine.ts,vite.config.mjs} "$stage/"\n'
                  "python3 - \"$stage\" \"$project/Sources/Resources\" <<'PY'\n"
                  'stage, resources = map(Path, sys.argv[1:])\n'
                  '(resources / "Engine.html").write_text("")\n'
                  'PY\n')
        self.assertEqual(script_references(script), {'ThirdParty/W/lunalyr-engine.ts',
                                                     'ThirdParty/W/lunalyr-vite.config.mjs', 'Sources/Resources',
                                                     'Sources/Resources/Engine.html'})
        notices = ('See [the manifest](ThirdParty/X/SOURCE.md) and [upstream](https://example.com/a). '
                   'Bundled as Sources/Resources/A-LICENSE.txt. Notices are in Resources/B-LICENSE.txt '
                   'and Verso-LICENSE.txt.')
        self.assertEqual(notice_references(notices), {'ThirdParty/X/SOURCE.md', 'Sources/Resources/A-LICENSE.txt',
                                                      'Sources/Resources/B-LICENSE.txt',
                                                      'Sources/Resources/Verso-LICENSE.txt'})


class MetalDeviceChoiceTests(unittest.TestCase):
    """On a dual-GPU MacBook Pro the system default Metal device is the
    discrete GPU, which it powers up for as long as it is held. The app asks
    for it only as VisualizerGPU's last resort, when no GPU can be listed;
    every renderer takes the GPU driving its display from VisualizerGPU, and
    Core Image is allowed the low-power GPU."""

    ALLOWED = {'Sources/Visualizers/VisualizerGPU.swift'}

    @staticmethod
    def sources():
        for source in sorted((ROOT / 'Sources').rglob('*.swift')):
            code = '\n'.join(line.split('//', 1)[0] for line in source.read_text().splitlines())
            yield source.relative_to(ROOT).as_posix(), code

    def test_only_the_gpu_chooser_asks_for_the_system_default_device(self):
        callers = {path for path, code in self.sources() if re.search(r'\bMTLCreateSystemDefaultDevice\s*\(', code)}
        self.assertEqual(callers - self.ALLOWED, set(),
                         'use VisualizerGPU.preferredDevice(for:) or VisualizerGPU.shared.context(for:)')

    CONTEXT = re.compile(r'\bCIContext\s*\(([^)]*)\)')

    @classmethod
    def default_contexts(cls, code):
        """Core Image contexts made without leave to use the low-power GPU."""
        return [match.group(0) for match in cls.CONTEXT.finditer(code)
                if 'allowLowPower' not in match.group(1) and 'mtlDevice' not in match.group(1)]

    def test_core_image_contexts_may_use_the_low_power_gpu(self):
        # A default CIContext renders on the highest-performance GPU, so the
        # artwork palette powered up the discrete GPU on every track change.
        self.assertEqual(self.default_contexts('let context = CIContext()'), ['CIContext()'])
        self.assertEqual(self.default_contexts('CIContext(options: [.allowLowPower: true,\n  .name: "x"])'), [])
        sources = dict(self.sources())
        self.assertNotRegex(sources['Sources/UI/ColorExtractor.swift'], self.CONTEXT,
                            'the artwork palette samples its small bitmap on the CPU')
        offenders = [f'{path}: {call}' for path, code in sources.items() for call in self.default_contexts(code)]
        self.assertEqual(offenders, [], 'pass options: [.allowLowPower: true], or CIContext(mtlDevice:) '
                                        'with VisualizerGPU.preferredDevice(for:)')


class ShellScriptSyntaxTests(unittest.TestCase):
    """CI cannot run locally, so at least every script it calls must parse."""

    def test_scripts_parse(self):
        scripts = sorted((ROOT / 'Scripts').glob('*.sh')) + sorted(ROOT.glob('*.command'))
        self.assertTrue(scripts)
        for script in scripts:
            with self.subTest(script=script.name):
                result = subprocess.run(['bash', '-n', str(script)], capture_output=True, text=True, timeout=60)
                self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == '__main__':
    unittest.main()
