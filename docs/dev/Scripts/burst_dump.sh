#!/bin/bash
# Dumps the Burst assembly of every job in Saesentsessis.Fuluma.Runtime for one target, and cuts out one job's body.
# Headless: runs the Editor's own Burst compiler (Burst 2.0 ships in the Editor, not in the package) on the IL-processed
# assemblies in Library/ScriptAssemblies, so the Editor must have compiled the scripts after the last edit (a batch-mode
# compile or test run does it). Safety checks off, as in players.
#
# usage: burst_dump.sh <target> <platform> <out-dir> [job-name]
#   x64:   X64_SSE2 Windows | X64_SSE4 Windows | AVX2 Windows
#   arm64: ARMV8A_AARCH64 Android
#   job-name defaults to SpotLightCullingJob; its body is written to <out-dir>/<job>_<target>.s
# needs: python on PATH. Pass UNITY_EDITOR=<.../Editor> for another Editor than 6000.7.0b4.
set -e
TARGET=$1; PLATFORM=$2; OUT=$3; JOB=${4:-SpotLightCullingJob}
EDITOR=${UNITY_EDITOR:-"C:/Program Files/Unity/Hub/Editor/6000.7.0b4/Editor"}
DATA="$EDITOR/Data"
PROJECT="$(cd "$(dirname "$0")/../../../Fuluma" && pwd)"   # Fuluma/ (the Unity project)
WORK="$OUT/work-$TARGET"

mkdir -p "$WORK/asm" "$WORK/out" "$WORK/tmp"
cp -f "$PROJECT"/Library/ScriptAssemblies/*.dll "$WORK/asm/"
cp -f "$PROJECT"/Library/PackageCache/com.unity.collections@*/Unity.Collections.LowLevel.ILSupport/Unity.Collections.LowLevel.ILSupport.dll "$WORK/asm/"

cat > "$WORK/asm.rsp" <<RSP
--platform=$PLATFORM
--target=$TARGET
--root-assembly=$WORK/asm/Saesentsessis.Fuluma.Runtime.dll
--assembly-folder=$WORK/asm
--assembly-folder=$DATA/Managed/UnityEngine
--assembly-folder=$DATA/Managed
--assembly-folder=$DATA/MonoBleedingEdge/lib/mono/unityjit-win32
--assembly-folder=$DATA/MonoBleedingEdge/lib/mono/unityjit-win32/Facades
--global-safety-checks-setting=Off
--opt-level=2
--float-mode=Fast
--debug=LineOnly
--dump=Asm
--output=$WORK/out/lib_burst_generated
--temp-folder=$WORK/tmp
--cache-directory=$WORK/cache
RSP

# bcl.exe needs a .NET 10 host; the Editor's DotNetSdk has one. Without --cache-directory (above) bcl writes
# Library/BurstCache into its current directory, so it also runs from the work folder (run from the package, Unity
# would import the cache as assets). See ~/.claude/docs/unity-burst.md.
(cd "$WORK" && "$DATA/DotNetSdk/dotnet.exe" "$DATA/Tools/Burst/Client/bcl/bcl.dll" "@$WORK/asm.rsp") > "$OUT/all_$TARGET.s" 2>&1

python - "$OUT/all_$TARGET.s" "$OUT/${JOB}_$TARGET.s" "$JOB" <<'PY'
import re, sys
src, dst, job = sys.argv[1:4]
lines = open(src, encoding='utf-8', errors='replace').read().split('\n')
start = next(i for i, l in enumerate(lines)
             if job + '>.Execute' in l and l.rstrip().endswith(':') and not l.startswith('\t'))
end = next(i for i in range(start, len(lines)) if lines[i].startswith('.Lfunc_end'))
body = [l for l in lines[start:end + 1]
        if not re.match(r'\s*\.(cv_|seh_|cfi_|loc\b|p2align)', l) and not l.startswith('.Ltmp')]
open(dst, 'w').write('\n'.join(body))
print(dst, 'instructions:', sum(1 for l in body if l.startswith('\t') and not l.strip().startswith('.')))
PY
