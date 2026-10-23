# Security policy

## Reporting a vulnerability

Please do not report security vulnerabilities in public issues. Use GitHub's private vulnerability reporting
instead: on the repository, go to **Security → Report a vulnerability**. We aim to respond within a few days,
and will agree on a disclosure date with you once a fix is ready.

## Supported versions

Until 1.0, only the latest released version receives security fixes.

## Handling credentials

The `runsignal` CLI needs a New Relic User API key, and the SDKs' exporters need an ingest (license) key. Pass
them through environment variables or your CI's secret store (`NEW_RELIC_API_KEY`), not on the command line or
in manifests. RunSignal never writes keys to manifests, logs or the resources it creates.
