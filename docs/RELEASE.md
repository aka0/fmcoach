# Release v0.7.30

The prepared local deliverables are in `dist/` (excluded from Git):

- `FMCoach-v0.7.30.zip`: the DLL, installation guide, prompt template and MIT license. No game assemblies or credentials.
- `FMCoach-v0.7.30-source.zip`: the public source tree, documentation, screenshots, portraits and tests. No local build dependencies, runtime data or Git metadata.
- `SHA256SUMS.txt`: checksums of both packages and the standalone DLL.

Version 0.7.30 moves the COACH launcher 58 logical pixels higher, keeping FM's bottom Apply / Cancel action row clear. The standalone DLL's SHA-256 and testing status are recorded in [verification notes](VERIFICATION.md).

The source repository is [aka0/fmcoach](https://github.com/aka0/fmcoach), using the `main` branch. Binary ZIPs remain local until explicitly attached to a GitHub release. For a release tagged `v0.7.30`, GitHub also provides source downloads from the tag automatically.

Before a future release, rebuild, run the offline checks, then verify the candidate inside FM against actual game data. Do not publish local `ref/`, `interop/`, save files, full-screen test captures, API keys or chat snapshots.
