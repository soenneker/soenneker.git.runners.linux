#!/bin/bash
set -euo pipefail

stage=$(realpath "$1")
mkdir -p "$stage/lib"
unset LD_LIBRARY_PATH

# ldd includes transitive dependencies. Inspect every ELF, including remote helpers.
while IFS= read -r -d '' binary; do
    if ! file -b "$binary" | grep -q ELF; then
        continue
    fi
    dependencies=$(ldd "$binary")
    if grep -q 'not found' <<< "$dependencies"; then
        printf 'Unresolved dependencies for %s:\n%s\n' "$binary" "$dependencies" >&2
        exit 1
    fi
    while read -r library; do
        name=$(basename "$library")
        # glibc and its loader must come from the runtime as a matching set.
        case "$name" in
            ld-linux*|libc.so.*|libm.so.*|libdl.so.*|libpthread.so.*|librt.so.*|libresolv.so.*|libutil.so.*|libanl.so.*)
                continue ;;
        esac
        # Dereference symlinks: the NuGet payload needs actual files under SONAMEs.
        cp -L "$library" "$stage/lib/$name"
    done < <(awk '$2 == "=>" && $3 ~ /^\// {print $3}' <<< "$dependencies")
done < <(find "$stage/usr" -type f -print0)

test -f "$stage/lib/libcurl.so.4"

# Fail if the bundle still resolves any non-glibc dependency from the build host.
while IFS= read -r -d '' binary; do
    if ! file -b "$binary" | grep -q ELF; then
        continue
    fi
    dependencies=$(LD_LIBRARY_PATH="$stage/lib" ldd "$binary")
    if grep -q 'not found' <<< "$dependencies"; then
        printf '%s\n' "$dependencies" >&2
        exit 1
    fi
    while read -r library; do
        case "$(basename "$library")" in
            ld-linux*|libc.so.*|libm.so.*|libdl.so.*|libpthread.so.*|librt.so.*|libresolv.so.*|libutil.so.*|libanl.so.*)
                continue ;;
        esac
        case "$library" in
            "$stage/lib/"*) ;;
            *) printf 'Dependency escaped bundle: %s\n' "$library" >&2; exit 1 ;;
        esac
    done < <(awk '$2 == "=>" && $3 ~ /^\// {print $3}' <<< "$dependencies")
done < <(find "$stage/usr" -type f -print0)
