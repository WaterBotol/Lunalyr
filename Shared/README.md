# Shared lyric rules

The Windows repository owns the portable recording checks and mixed-timing rules in `shared/LyricRules`. This app compiles the same C files directly. Both apps exercise the checked-in fixtures through their normal lyric paths.

To update this copy, run `bash Scripts/sync-lyric-rules.sh /path/to/Lunalyr-Windows <commit>`. To verify the pinned revision and fixture data, run the same command with `--check` in place of the commit. The Mac app builds from the checked-in copy.
