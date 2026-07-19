# Releasing (maintainers)

Releases are automated by [semantic-release](https://semantic-release.gitbook.io/) — nobody edits
version numbers or tags by hand. The setup mirrors the rest of the eQuantic family
([core-linq](https://github.com/eQuantic/core-linq/blob/main/docs/releasing.md)).

## How a release happens

1. Commits land on `master` (house style: `emoji type: description`, e.g. `✨ feat: …` — the
   [release.config.mjs](../release.config.mjs) parser accepts the gitmoji prefix).
2. [release.yml](../.github/workflows/release.yml) runs the full test matrix
   (ubuntu/windows × net8.0/net10.0); only if green does the release job start, inside the
   `nuget` GitHub environment.
3. semantic-release analyzes the commits since the last `v*` tag:

   | Commits since last tag contain | Next version |
   |-------------------------------|--------------|
   | `✨ feat!:` or a `BREAKING CHANGE:` footer | major |
   | `✨ feat:` | minor |
   | `🐛 fix:` / `⚡ perf:` | patch |
   | only `docs`, `chore`, `ci`, `test`, `refactor`, `style` | **no release** |

4. If a release is due, the pipeline updates `CHANGELOG.md`, stamps the version into
   `src/Directory.Build.props`, packs the package with `-p:Version` + `ContinuousIntegrationBuild`,
   commits those files back (`🔧 chore: release vX.Y.Z [skip ci]`), pushes to NuGet.org, tags
   `vX.Y.Z` and creates the GitHub release with the `.nupkg`/`.snupkg` attached.

## Baseline

`v5.0.0` is the annotated **baseline marker** at the v5 contract redesign. semantic-release counts
from it, so the first automated release is a minor (`5.1.0`) carrying the fluent `QueryOptions`
additions. It replaces the previous `dotnetcore.yml` publish-on-every-push workflow, whose static
version made pushes silent no-ops on NuGet.

## Arming and gating

Without the `NUGET_KEY` secret, releases are **disarmed**:
[release-verify.sh](../.github/scripts/release-verify.sh) fails the pipeline before anything is
tagged, committed or published. Add a required reviewer on the `nuget` environment (Settings →
Environments → `nuget`) for a manual approval gate before each release.

## Channels

- `master` → stable releases.
- `preview` branch → prerelease channel (`X.Y.Z-preview.N`).

## Day-to-day rules

- Never push tags by hand; never edit `<Version>` in `src/Directory.Build.props` expecting it to be
  released — the pipeline overwrites it.
- Mark breaking changes with `✨ feat!: …` and/or a `BREAKING CHANGE:` paragraph.
- Secrets/config: `NUGET_KEY` is the eQuantic org secret; `GITHUB_TOKEN` is the workflow's own.
