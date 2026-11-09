#!/bin/bash
set -euo pipefail

stage=$(realpath "$1")
# Match the build host's glibc baseline, but install no Git or curl packages.
. /etc/os-release
test "$ID" = ubuntu
docker run --rm --mount "type=bind,source=$stage,target=/bundle,readonly" \
    --mount type=bind,source=/etc/ssl/certs,target=/etc/ssl/certs,readonly \
    "ubuntu:$VERSION_ID" bash -euo pipefail -c '
        test -f /bundle/lib/libcurl.so.4
        test -d /bundle/usr/share/git-core/templates
        /bundle/git.sh init /tmp/template-test
        test -f /tmp/template-test/.git/hooks/pre-commit.sample
        /bundle/git.sh clone --depth 1 https://github.com/git/git /tmp/clone-test
        /bundle/git.sh -C /tmp/clone-test rev-parse --verify HEAD
    '
