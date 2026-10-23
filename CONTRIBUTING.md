# Contributing to RunSignal

Thank you for helping. Issues and pull requests are welcome.

## Before you start

- For a bug, open an issue with the language, versions (SDK, OpenTelemetry, scheduler) and a minimal way to
  reproduce it.
- For a new feature or integration, open an issue first, so we can agree on the design before you write code.

## Keeping the SDKs consistent

RunSignal's promise is one model in five languages. A change to what an SDK emits must be made in
[spec/README.md](spec/README.md) first, and then in every SDK. The conformance vectors in
[spec/conformance](spec/conformance) are run by every SDK's test suite. If you change the signals, update
`signals.json`, and make every SDK pass it.

## Building and testing

See [Building and testing](README.md#building-and-testing) for each language. CI runs all of them, including the
Java suite against Spring Boot 3.2, 3.5 and 4.1 and the Prometheus rules with `promtool test rules`.

Please include tests with your change and keep each language's formatter happy: `dotnet format`, `gofmt`,
`cargo fmt` and `cargo clippy`. Warnings are treated as errors in .NET and Java.

## Pull requests

- Keep a pull request to one change, with a description of what changed and why.
- Update the README and the language's own documentation in the same pull request.
- Add a line to [CHANGELOG.md](CHANGELOG.md) under the unreleased version.

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
