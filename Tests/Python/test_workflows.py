"""The GitHub workflows cannot run locally, so their release wiring is checked here.

Environment secrets reach only the jobs that name their environment. A job that
signed with the certificate without naming the "release" environment would
therefore quietly build ad hoc again once the owner moved the secrets there, as
the setup guide recommends, and every user would lose their System Audio /
Screen Recording permission with the next update.
"""
import re
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = sorted((ROOT / '.github' / 'workflows').glob('*.yml'))
RELEASE_SECRETS = ('SPARKLE_PRIVATE_KEY', 'MACOS_CODESIGN_P12', 'MACOS_CODESIGN_P12_PASSWORD',
                   'MACOS_CODESIGN_IDENTITY')
# The scripts CI runs for the native tests and for the app build, under their
# current names and the Scripts/test.sh / Scripts/build.sh names they move to.
TEST_SCRIPTS = ('Run-Tests.command', 'Scripts/test.sh')
BUILD_SCRIPTS = ('Build.command', 'Scripts/build-custom.sh', 'Scripts/build.sh')
STEP_LOGS = ((TEST_SCRIPTS, 'Build/test.log'), (BUILD_SCRIPTS, 'Build/build.log'))
# What a job needs besides its timed steps: checkout, tool setup and the log
# upload (about 35 seconds on the macos-15 runners).
SETUP_MINUTES = 5


def jobs(text):
    """Each job's text by name, for the two-space layout these workflows use."""
    found, current, inside = {}, None, False
    for line in text.splitlines():
        if re.match(r'^\S', line):
            inside, current = line.startswith('jobs:'), None
            continue
        match = re.match(r'^  ([\w-]+):\s*$', line) if inside else None
        if match:
            current = match.group(1)
            found[current] = []
        elif current:
            found[current].append(line)
    return {name: '\n'.join(lines) for name, lines in found.items()}


def steps(job):
    """A job's steps as text blocks, in order."""
    return re.split(r'(?m)^      - ', job)[1:]


def declared_inputs(text):
    names, inside = set(), False
    for line in text.splitlines():
        if re.match(r'^    inputs:\s*$', line):
            inside = True
        elif inside and line.strip() and len(line) - len(line.lstrip()) <= 4:
            inside = False
        elif inside and re.match(r'^      ([\w-]+):\s*$', line):
            names.add(line.strip().rstrip(':'))
    return names


def step_keys(step):
    """A step's own keys (name, if, env, run...) mapped to their inline value plus the lines nested under them."""
    keys, current = {}, None
    # `steps()` leaves the first key at column 0 and the others at eight spaces.
    for line in (' ' * 8 + step).splitlines():
        indent = len(line) - len(line.lstrip())
        if not line.strip() or (indent < 9 and line.lstrip().startswith('#')):
            continue
        match = re.match(r' {8}([\w-]+):\s*(.*)$', line) if indent == 8 else None
        if match:
            current = match.group(1)
            keys[current] = match.group(2)
        elif current and indent > 8:
            keys[current] += '\n' + line
        else:
            current = None
    return keys


def step_env(keys):
    """The variables a step's `env:` block sets, unquoted."""
    return {name: value.strip('\'"') for name, value in
            re.findall(r'(?m)^ {10}([A-Za-z_]\w*):[ \t]*(.*?)[ \t]*$', keys.get('env', ''))}


def runs_any(keys, scripts):
    return any(script in keys.get('run', '') for script in scripts)


def job_timeout(job):
    match = re.search(r'(?m)^    timeout-minutes:\s*(\d+)\s*$', job.split('\n    steps:')[0])
    return int(match.group(1)) if match else None


def unguarded_finder_calls(script):
    """Line numbers of `open` commands outside the branch of an `if [[ -z "${CI:-}" ]]`."""
    ci_unset = r'(?:el)?if \[\[ -z "\$\{CI:-\}" \]\]'
    guards, found = [], []
    for number, line in enumerate(script.splitlines(), 1):
        code = line.strip()
        if re.match(r'if\s', code) and not re.search(r'\bfi\s*$', code):
            guards.append(bool(re.match(ci_unset, code)))
        elif re.match(r'(?:elif|else)\b', code) and guards:
            guards[-1] = bool(re.match(ci_unset, code))
        elif re.match(r'fi\b', code) and guards:
            guards.pop()
        elif re.match(r'(?:/usr/bin/)?open\s', code) and not any(guards):
            found.append(number)
    return found


class WorkflowTests(unittest.TestCase):
    def test_release_secrets_are_read_only_by_the_main_branch_release_job(self):
        readers = set()
        for workflow in WORKFLOWS:
            for name, job in jobs(workflow.read_text()).items():
                used = [secret for secret in RELEASE_SECRETS if re.search(rf'\bsecrets\.{secret}\b', job)]
                if not used:
                    continue
                readers.add(f'{workflow.name}:{name}')
                header = job.split('\n    steps:')[0]
                with self.subTest(job=f'{workflow.name}:{name}'):
                    self.assertRegex(header, r'(?m)^    environment: release\s*$',
                                     f'{name} reads {used} outside the release environment')
                    self.assertIn("github.ref == 'refs/heads/main'", header, f'{name} must only run on main')
        self.assertEqual(readers, {'macos.yml:draft-release'})

    def test_release_job_signs_the_tested_build_right_before_packaging_it(self):
        release = steps(jobs((ROOT / '.github' / 'workflows' / 'macos.yml').read_text())['draft-release'])

        def position(command):
            found = [index for index, step in enumerate(release) if command in step]
            self.assertEqual(len(found), 1, f'expected one draft-release step running {command}')
            return found[0]

        order = [position(command) for command in (
            'ditto -x -k Build/Lunalyr-Mac.zip', 'ci.sh import-signing-identity', 'sign-app.sh Build/Lunalyr.app',
            'ci.sh remove-signing-identity', 'package-update.sh', 'gh release create')]
        self.assertEqual(order, sorted(order))
        self.assertIn('if: always()', release[position('ci.sh remove-signing-identity')])

    def test_every_input_a_workflow_uses_is_declared(self):
        for workflow in WORKFLOWS:
            text = workflow.read_text()
            used = set(re.findall(r'\binputs\.([\w-]+)', text))
            with self.subTest(workflow=workflow.name):
                self.assertLessEqual(used, declared_inputs(text))
        self.assertEqual(declared_inputs((ROOT / '.github' / 'workflows' / 'macos.yml').read_text()),
                         {'prepare_release', 'first_update', 'new_signing_identity'})

    def test_test_and_build_steps_time_out_before_their_job_and_keep_their_logs(self):
        """A job timeout cancels the whole job, so its `if: always()` log upload never starts: run
        41917259770 hung in the build step until the job limit and lost Build/build.log. A step
        timeout fails only that step, and the upload still runs."""
        def always_uploads(step):
            condition = step.get('if', '').strip('\'"')
            return ('actions/upload-artifact@' in step.get('uses', '')
                    and re.fullmatch(r'(\$\{\{\s*)?always\(\)(\s*\}\})?', condition))

        checked = set()
        for workflow in WORKFLOWS:
            for name, job in jobs(workflow.read_text()).items():
                where, limits = f'{workflow.name}:{name}', []
                job_steps = [step_keys(step) for step in steps(job)]
                for index, step in enumerate(job_steps):
                    logs = [log for scripts, log in STEP_LOGS if runs_any(step, scripts)]
                    if not logs:
                        continue
                    checked.add(where)
                    with self.subTest(step=f'{where}:{step.get("name")}'):
                        self.assertRegex(step.get('timeout-minutes', ''), r'^\d+$', 'needs its own timeout-minutes')
                        limits.append(int(step['timeout-minutes']))
                        uploaded = '\n'.join(later.get('with', '') for later in job_steps[index + 1:]
                                             if always_uploads(later))
                        for log in logs:
                            self.assertIn(log, uploaded, f'a later `if: always()` step must upload {log}')
                if limits:
                    with self.subTest(job=where):
                        self.assertIsNotNone(job_timeout(job), 'the job needs a timeout-minutes of its own')
                        self.assertLessEqual(sum(limits) + SETUP_MINUTES, job_timeout(job) or 0,
                                             'the step limits must leave the job time to upload its logs')
        self.assertEqual(checked, {'macos.yml:build', 'release.yml:release'})

    def test_native_tests_fail_instead_of_skipping_without_a_gpu(self):
        """The macos-15 runners have a Metal device (every run so far rendered the GPU scene tests), so
        a runner without one must fail instead of passing with every GPU and WebKit test skipped.
        xcodebuild passes the test process only TEST_RUNNER_ variables, with the prefix removed."""
        support = (ROOT / 'Tests' / 'Swift' / 'GPUTestSupport.swift').read_text()
        read = re.search(r'requirementVariables\s*=\s*\[([^\]]*)\]', support)
        self.assertIsNotNone(read, 'GPUTestSupport.requirementVariables is gone')
        self.assertIn('"LUNALYR_REQUIRE_GPU"', read.group(1), 'the name the tests see after xcodebuild')
        testing = set()
        for workflow in WORKFLOWS:
            text = workflow.read_text()
            with self.subTest(workflow=workflow.name):
                self.assertEqual(re.findall(r'(?m)^\s*(?:export\s+)?LUNALYR_REQUIRE_GPU\s*[:=]', text), [],
                                 'a bare LUNALYR_REQUIRE_GPU never reaches the tests through xcodebuild')
            for name, job in jobs(text).items():
                for step in map(step_keys, steps(job)):
                    if runs_any(step, TEST_SCRIPTS):
                        testing.add(f'{workflow.name}:{name}')
                        with self.subTest(step=f'{workflow.name}:{name}:{step.get("name")}'):
                            self.assertEqual(step_env(step).get('TEST_RUNNER_LUNALYR_REQUIRE_GPU'), '1')
        self.assertEqual(testing, {'macos.yml:build', 'release.yml:release'})

    def test_builds_reuse_the_packages_the_tests_resolved_and_cached(self):
        """Resolving the Swift packages again over the network is one suspect for the build step that
        hung in run 41917259770, so the build reuses the folder the tests filled and the cache restores."""
        test_script = next((ROOT / name for name in TEST_SCRIPTS if (ROOT / name).exists()), None)
        self.assertIsNotNone(test_script, f'none of {TEST_SCRIPTS} exists')
        derived = re.search(r'-derivedDataPath\s+(\S+)', test_script.read_text())
        self.assertIsNotNone(derived, f'{test_script.name} no longer sets -derivedDataPath')
        packages = derived.group(1).strip('"\'') + '/SourcePackages'
        building = set()
        for workflow in WORKFLOWS:
            for name, job in jobs(workflow.read_text()).items():
                job_steps = [step_keys(step) for step in steps(job)]
                builds = [step for step in job_steps if runs_any(step, BUILD_SCRIPTS)]
                if not builds:
                    continue
                building.add(f'{workflow.name}:{name}')
                with self.subTest(job=f'{workflow.name}:{name}'):
                    cached = [re.findall(r'(?m)^\s*path:\s*(\S+)\s*$', step.get('with', ''))
                              for step in job_steps if 'actions/cache@' in step.get('uses', '')]
                    self.assertEqual(cached, [[packages]])
                    for step in builds:
                        self.assertEqual(step_env(step).get('LUNALYR_SOURCE_PACKAGES_DIR'),
                                         '${{ github.workspace }}/' + packages)
        self.assertEqual(building, {'macos.yml:build', 'release.yml:release'})

    def test_build_script_reuses_resolved_packages_and_skips_finder_on_ci(self):
        """Revealing the app in Finder is for people building locally; runners set CI, and the
        renamed Scripts/build.sh must keep both behaviours."""
        scripts = [path for path in (ROOT / 'Scripts' / 'build-custom.sh', ROOT / 'Scripts' / 'build.sh')
                   if path.exists()]
        self.assertTrue(scripts)
        for script in scripts:
            text = script.read_text()
            with self.subTest(script=script.name, check='Finder'):
                self.assertEqual(unguarded_finder_calls(text), [], 'wrap `open` in if [[ -z "${CI:-}" ]]')
            with self.subTest(script=script.name, check='packages'):
                folder = re.search(r'(?m)^\s*(\w+)="\$\{LUNALYR_SOURCE_PACKAGES_DIR:-[^"]*\}"', text)
                self.assertIsNotNone(folder, 'LUNALYR_SOURCE_PACKAGES_DIR is ignored')
                self.assertRegex(text, rf'-clonedSourcePackagesDirPath\s+"\${folder.group(1)}"')

    def test_step_and_script_parsing(self):
        job = ('    timeout-minutes: 40\n    steps:\n'
               '      - name: Native tests\n        timeout-minutes: 20\n        env:\n'
               "          TEST_RUNNER_X: '1'\n          OTHER: \"a b\"\n        run: |\n"
               '          # not a key: value\n          bash Run-Tests.command\n'
               '      # Between steps\n      - uses: actions/upload-artifact@abc # v1\n        if: always()\n'
               '        with:\n          path: |\n            Build/test.log\n')
        native, upload = map(step_keys, steps(job))
        self.assertEqual(job_timeout(job), 40)
        self.assertEqual((native['name'], native['timeout-minutes']), ('Native tests', '20'))
        self.assertEqual(step_env(native), {'TEST_RUNNER_X': '1', 'OTHER': 'a b'})
        self.assertTrue(runs_any(native, TEST_SCRIPTS))
        self.assertFalse(runs_any(native, BUILD_SCRIPTS))
        self.assertEqual(upload['if'], 'always()')
        self.assertIn('Build/test.log', upload['with'])
        self.assertNotIn('timeout-minutes', upload)
        guarded = 'if [[ -z "${CI:-}" ]]; then\n  open -R "$APP"\nfi\n'
        self.assertEqual(unguarded_finder_calls(guarded), [])
        self.assertEqual(unguarded_finder_calls('echo "open it"\nopen -R "$APP"\n'), [2])
        self.assertEqual(unguarded_finder_calls('if [[ -e "$APP" ]]; then\n  open "$APP"\nfi\n'), [2])
        self.assertEqual(unguarded_finder_calls(guarded + 'if [[ -e x ]]; then :; fi\nopen -R "$APP"\n'), [5])
        self.assertEqual(unguarded_finder_calls('if [[ -z "${CI:-}" ]]; then\n  :\nelse\n  open "$APP"\nfi\n'), [4])

    @unittest.skipUnless(shutil.which('ruby'), 'needs Ruby to parse YAML')
    def test_workflows_and_project_spec_are_valid_yaml(self):
        for path in WORKFLOWS + [ROOT / 'project.yml']:
            with self.subTest(path=path.name):
                result = subprocess.run(['ruby', '-ryaml', '-e', 'YAML.load_file(ARGV[0])', str(path)],
                                        capture_output=True, text=True, timeout=60)
                self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == '__main__':
    unittest.main()
