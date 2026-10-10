param(
    [string]$SourcePlugin = "Assets/Plugins/Android/arm64-v8a/libArmCameraGStreamer.so",
    [string]$DestinationDirectory = "Assets/Plugins/Android/arm64-v8a",
    [ValidateRange(1, 4)]
    [int]$ReceiverCount = 4
)

$source = (Resolve-Path -LiteralPath $SourcePlugin).Path
$destination = (Resolve-Path -LiteralPath $DestinationDirectory).Path
$originalSoname = [Text.Encoding]::ASCII.GetBytes("libArmCameraGStreamer.so")

for ($slot = 1; $slot -lt $ReceiverCount; ++$slot) {
    $newSonameText = "libArmCameraGStream{0:D2}.so" -f $slot
    $newSoname = [Text.Encoding]::ASCII.GetBytes($newSonameText)
    if ($newSoname.Length -ne $originalSoname.Length) {
        throw "Replacement SONAME must have the same length as the original SONAME."
    }

    $target = Join-Path $destination $newSonameText
    Copy-Item -LiteralPath $source -Destination $target -Force
    $bytes = [IO.File]::ReadAllBytes($target)
    $replacements = 0

    for ($i = 0; $i -le $bytes.Length - $originalSoname.Length; ++$i) {
        $matches = $true
        for ($j = 0; $j -lt $originalSoname.Length; ++$j) {
            if ($bytes[$i + $j] -ne $originalSoname[$j]) {
                $matches = $false
                break
            }
        }
        if (-not $matches) {
            continue
        }

        [Array]::Copy($newSoname, 0, $bytes, $i, $newSoname.Length)
        ++$replacements
        $i += $originalSoname.Length - 1
    }

    if ($replacements -ne 1) {
        throw "Expected one SONAME in $target, found $replacements."
    }

    [IO.File]::WriteAllBytes($target, $bytes)
    Write-Host "Created $target with SONAME $newSonameText"
}
