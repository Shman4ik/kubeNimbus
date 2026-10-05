#!/bin/bash
# make-gif.sh in.mp4 out.gif [seconds] [start] [fps] [width] : two-pass palette GIF, the recipe from README.md.
# `-update 1 -frames:v 1` on the palette pass is required on this ffmpeg build.
in="$1"; out="$2"; t="${3:-12}"; ss="${4:-0.4}"; fps="${5:-10}"; w="${6:-1000}"
pal="$(mktemp -u).png"
vf="fps=$fps,scale=$w:-1:flags=lanczos"
ffmpeg -loglevel error -y -ss "$ss" -t "$t" -i "$in" -vf "$vf,palettegen=max_colors=128:stats_mode=diff" -update 1 -frames:v 1 "$pal" &&
ffmpeg -loglevel error -y -ss "$ss" -t "$t" -i "$in" -i "$pal" -filter_complex "[0:v]$vf[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle" "$out" &&
echo "$out $(( $(stat -c %s "$out") / 1024 )) KB"
rm -f "$pal"
