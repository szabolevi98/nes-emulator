# Public test programs only. Nothing from these downloads is committed.
[CmdletBinding()]
param([switch]$IncludeCpuVectors, [switch]$IncludeAccuracyCoin)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$romRoot = Join-Path $projectRoot 'roms/accuracy'
$romRevision = '95d8f621ae55cee0d09b91519a8989ae0e64753b'
$vectorRevision = '2f6980a2d95757486c7bee24355c360e40e2a224'

if (!(Test-Path -LiteralPath $romRoot)) {
    git clone https://github.com/christopherpow/nes-test-roms.git $romRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the accuracy ROM repository.' }
    git -C $romRoot checkout --detach $romRevision
    if ($LASTEXITCODE -ne 0) { throw 'Could not check out the pinned ROM revision.' }
}
else {
    $actualRevision = git -C $romRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $romRevision) {
        throw "Existing ROM directory must be at $romRevision; its contents were left untouched."
    }
    $changes = git -C $romRoot status --porcelain --untracked-files=no
    if ($changes) { throw 'The downloaded ROM repository has modified files.' }
}
Write-Host "Accuracy ROMs ready at $romRoot ($romRevision)."

if ($IncludeAccuracyCoin) {
    $coinRoot = Join-Path $projectRoot 'roms/accuracy-coin'
    $coinRevision = '9bc42d1e3acbeeaea215b1011d58f4ce72a8a49e'
    if (!(Test-Path -LiteralPath $coinRoot)) {
        git clone https://github.com/100thCoin/AccuracyCoin.git $coinRoot
        if ($LASTEXITCODE -ne 0) { throw 'Could not download AccuracyCoin.' }
        git -C $coinRoot checkout --detach $coinRevision
        if ($LASTEXITCODE -ne 0) { throw 'Could not check out the pinned AccuracyCoin revision.' }
    }
    else {
        $actualRevision = git -C $coinRoot rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $coinRevision) {
            throw "Existing AccuracyCoin directory must be at $coinRevision; its contents were left untouched."
        }
        $changes = git -C $coinRoot status --porcelain --untracked-files=no
        if ($changes) { throw 'The downloaded AccuracyCoin repository has modified files.' }
    }
    Write-Host "AccuracyCoin ready at $coinRoot ($coinRevision)."
}

if ($IncludeCpuVectors) {
    # Roughly 1 GB: optional so the quick local test suite stays small and offline.
    $vectorRoot = Join-Path $projectRoot 'roms/cpu-vectors'
    New-Item -ItemType Directory -Force -Path $vectorRoot | Out-Null
    for ($opcode = 0; $opcode -lt 256; $opcode++) {
        $name = '{0:x2}.json' -f $opcode
        $destination = Join-Path $vectorRoot $name
        $temporary = "$destination.download"
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/SingleStepTests/65x02/$vectorRevision/nes6502/v1/$name" -OutFile $temporary
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        Write-Progress -Activity 'Downloading CPU bus-cycle vectors' -Status $name -PercentComplete (($opcode + 1) / 256 * 100)
    }
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/SingleStepTests/65x02/$vectorRevision/LICENSE" -OutFile (Join-Path $vectorRoot 'LICENSE')
    Set-Content -LiteralPath (Join-Path $vectorRoot 'revision.txt') -Value $vectorRevision
    Write-Host "All 256 CPU vector files ready ($vectorRevision)."
}
