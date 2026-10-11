# Builds the Store trailer from the three committed Windows demo recordings.
# No app is launched: these GIFs already contain real input on the demo cluster.
# Requires ffmpeg on PATH. Output is checked into design/store/trailer/.
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutDir) { $OutDir = Join-Path $repo 'design/store/trailer' }
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('kubenimbus-trailer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

function Invoke-Encoder([string[]]$Arguments) {
    & $ffmpeg -hide_banner -loglevel error -y @Arguments
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited with code $LASTEXITCODE." }
}

# Match pgNimbus's navy canvas, caption placement and quiet transitions. The UI
# recordings stay at their original 10 fps; encoding at 30 fps permits smooth fades
# without inventing intermediate UI states. Their lower GIF resolution is documented.
$scenes = @(
    @{ File = 'applications-demo.gif'; Seconds = 12; Caption = 'Find the unhealthy app. See why.' },
    @{ File = 'palette-logs-demo.gif'; Seconds = 11; Caption = 'Ctrl+K opens commands and workload logs.' },
    @{ File = 'unhealthy-logs-demo.gif'; Seconds = 9; Caption = 'Filter unhealthy pods. Press L for logs.' }
)
$font = 'C\:/Windows/Fonts/seguisb.ttf'
$inputs = @()
$filters = @()

try {
    Push-Location $scratch
    try {
        for ($i = 0; $i -lt $scenes.Count; $i++) {
            $scene = $scenes[$i]
            $caption = "caption$i.txt"
            [IO.File]::WriteAllText((Join-Path $scratch $caption), $scene.Caption, [Text.UTF8Encoding]::new($false))
            $inputs += @('-ignore_loop', '1', '-t', [string]$scene.Seconds, '-i', (Join-Path $repo "design/screenshots/$($scene.File)"))
            $filters += "[$($i):v]fps=30,scale=1424:-2:flags=lanczos,setsar=1,format=yuv420p,pad=1920:1080:248:36:color=0x242b36,drawbox=x=247:y=35:w=1426:h=894:color=white@0.10:t=1,drawtext=fontfile='$font':textfile=$($caption):fontsize=40:fontcolor=white:x=(w-text_w)/2:y=1008-text_h/2,setpts=PTS-STARTPTS[s$i]"
        }

        [IO.File]::WriteAllText((Join-Path $scratch 'end.txt'), 'Free and open source. No account, no telemetry.', [Text.UTF8Encoding]::new($false))
        $inputs += @('-f', 'lavfi', '-t', '4', '-i', 'color=c=0x242b36:s=1920x1080:r=30', '-loop', '1', '-t', '4', '-i', (Join-Path $repo 'design/masters/logo/wordmark-dark.png'))
        $filters += "[4:v]scale=900:-2[logo]"
        $filters += "[3:v][logo]overlay=(W-w)/2:(H-h)/2-70:shortest=1,format=yuv420p,setsar=1,drawtext=fontfile='$font':textfile=end.txt:fontsize=42:fontcolor=0xAAB2C0:x=(w-text_w)/2:y=700[s3]"
        $filters += '[s0][s1]xfade=transition=fade:duration=0.5:offset=11.5[x1]'
        $filters += '[x1][s2]xfade=transition=fade:duration=0.5:offset=22[x2]'
        $filters += '[x2][s3]xfade=transition=fade:duration=0.5:offset=30.5,fade=t=in:st=0:d=0.4:color=0x242b36[v]'
        $inputs += @('-f', 'lavfi', '-t', '34.5', '-i', 'anullsrc=r=48000:cl=stereo')
        $video = Join-Path $OutDir 'kubenimbus-trailer.mp4'
        Invoke-Encoder ($inputs + @(
            '-filter_complex', ($filters -join ';'), '-map', '[v]', '-map', '5:a',
            '-c:v', 'libx264', '-preset', 'slow', '-crf', '18', '-profile:v', 'high',
            '-pix_fmt', 'yuv420p', '-r', '30', '-g', '15', '-bf', '2', '-flags', '+cgop',
            '-maxrate', '50M', '-bufsize', '100M', '-c:a', 'aac', '-b:a', '384k',
            '-shortest', '-movflags', '+faststart', $video
        ))
        Invoke-Encoder @('-ss', '5', '-i', $video, '-frames:v', '1', '-update', '1', (Join-Path $OutDir 'kubenimbus-trailer-thumbnail.png'))
        Write-Host "Wrote $video (34.5 seconds) and its 1920x1080 thumbnail."
    } finally { Pop-Location }
} finally {
    # Only the unique temporary directory created above may be removed.
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the temporary root: $resolvedScratch"
    }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
