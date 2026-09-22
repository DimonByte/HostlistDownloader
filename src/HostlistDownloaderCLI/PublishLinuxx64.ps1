$runtimes = @(
    "linux-x64"
)

$baseName = "HostDownloader"

foreach ($rid in $runtimes) {
    Write-Host "--- Publishing for $rid ---" -ForegroundColor Cyan
    
    $outputDir = "./publish/$rid"
    $customName = "$baseName-$rid"

    $dotnetArgs = @(
        "publish",
        "-c Release",
        "-f net10.0",
        "-r $rid",
        "--self-contained true",
        "-p:PublishSingleFile=true",
        "-p:PublishTrimmed=true",
        "-p:AssemblyName=$customName",
        "-o $outputDir"
    )

    $process = Start-Process -FilePath "dotnet" -ArgumentList $dotnetArgs -NoNewWindow -Wait -PassThru

    if ($process.ExitCode -ne 0) {
        Write-Host "Build failed for $rid (Exit Code: $($process.ExitCode)). Skipping checksum generation." -ForegroundColor Red
        continue
    }

    Write-Host "Build successful for $rid." -ForegroundColor Green

    if (-not (Test-Path $outputDir)) {
        Write-Host "Output directory $outputDir does not exist despite build success. Skipping checksums." -ForegroundColor Yellow
        continue
    }

    $files = Get-ChildItem $outputDir -File
    if ($files.Count -eq 0) {
        Write-Host "No files found in $outputDir. Skipping checksums." -ForegroundColor Yellow
        continue
    }

    Write-Host "Complete for $rid." -ForegroundColor Green
}