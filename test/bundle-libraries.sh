#!/bin/bash
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/usr/bin" "$stage/usr/libexec/git-core"
cp /usr/bin/git /usr/bin/curl "$stage/usr/bin/"
cp -L "$(git --exec-path)/git-remote-http" "$stage/usr/libexec/git-core/"
cp "$stage/usr/libexec/git-core/git-remote-http" "$stage/usr/libexec/git-core/git-remote-https"

bash "$repo/src/Soenneker.Git.Runners.Linux/Scripts/bundle-libraries.sh" "$stage"
test -f "$stage/lib/libcurl.so.4"
test ! -L "$stage/lib/libcurl.so.4"
test ! -e "$stage/lib/libc.so.6"
LD_LIBRARY_PATH="$stage/lib" "$stage/usr/bin/curl" --version
LD_LIBRARY_PATH="$stage/lib" "$stage/usr/bin/git" --version

# A dependency available at link time but absent during packaging must fail the build.
printf 'int missing(void) { return 0; }\n' > "$stage/missing.c"
printf 'extern int missing(void); int main(void) { return missing(); }\n' > "$stage/main.c"
gcc -shared -fPIC "$stage/missing.c" -Wl,-soname,libbundle-test-missing.so -o "$stage/libbundle-test-missing.so"
gcc "$stage/main.c" -L"$stage" -lbundle-test-missing -o "$stage/usr/bin/missing-dependency"
if bash "$repo/src/Soenneker.Git.Runners.Linux/Scripts/bundle-libraries.sh" "$stage"; then
    echo 'Packaging unexpectedly accepted an unresolved dependency' >&2
    exit 1
fi
