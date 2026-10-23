# Releasing

Every package is released together, with the same version, by
[.github/workflows/release.yml](../.github/workflows/release.yml).

## Cutting a release

1. Set the version everywhere: `Directory.Build.props` (`<Version>`), `src/RunSignal.Node/package.json`,
   `src/RunSignal.Rust/Cargo.toml`, and the `<version>` of the three POMs in `src/RunSignal.Java`.
2. In `CHANGELOG.md`, turn `## X.Y.Z (unreleased)` into `## X.Y.Z (YYYY-MM-DD)`.
3. Commit, then tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`.

The workflow refuses to publish if any package's version differs from the tag, or the changelog has no
section for it. It then:
- publishes to NuGet, npm, crates.io and Maven Central;
- tags the Go module as `go/vX.Y.Z`;
- creates a GitHub release with that version's changelog and the NuGet packages.

Each registry job can be re-run on its own. Pushes that already happened are skipped on NuGet; npm,
crates.io and Maven Central reject a version that already exists.

## Credentials

| Registry | First release | Afterwards |
|---|---|---|
| NuGet | `NUGET_API_KEY`: a key scoped to `RunSignal.*` with "Push new packages and package versions" | Trusted Publishing: add a policy for this repository and `release.yml`, set `NUGET_USER` to your nuget.org profile name, delete `NUGET_API_KEY` |
| npm | `NPM_TOKEN`: a granular token with read and write on `@runsignal`, bypassing 2FA | Trusted Publishing on the package (Settings → Trusted publishing → GitHub Actions, `release.yml`), then delete `NPM_TOKEN` |
| crates.io | `CARGO_REGISTRY_TOKEN`: scopes `publish-new` and `publish-update`, crate pattern `runsignal` | Trusted Publishing on the crate (Settings → Trusted Publishing), then delete the token and revoke it |
| Maven Central | `MAVEN_CENTRAL_USERNAME`, `MAVEN_CENTRAL_PASSWORD` (a Central Portal user token), `MAVEN_GPG_PRIVATE_KEY`, `MAVEN_GPG_PASSPHRASE` | The same: Maven Central has no Trusted Publishing |
| Go | none | none |

A job uses its token secret when the secret exists, and Trusted Publishing when it does not. npm and
crates.io only allow Trusted Publishing for packages that already exist, so the first release needs their
tokens.
