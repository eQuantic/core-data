#!/usr/bin/env bash
# semantic-release verifyConditions step: refuse to start a release without a NuGet key, BEFORE
# anything is analyzed, tagged, committed or published. The key is the one-hour key the workflow's
# Trusted Publishing login got from nuget.org.
set -euo pipefail

if [ -z "${NUGET_KEY:-}" ]; then
  {
    echo "NUGET_KEY is empty, so releases are disarmed."
    echo "The workflow's 'Log in to nuget.org' step gave no key: check the Trusted Publishing"
    echo "policy on nuget.org (owner eQuantic, repository core-data, workflow release.yml,"
    echo "environment nuget) and the NUGET_USER secret, then re-run the workflow."
  } >&2
  exit 1
fi
