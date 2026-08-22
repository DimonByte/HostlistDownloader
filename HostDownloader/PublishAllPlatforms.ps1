$runtimes = @(
    "win-x64",
    "win-arm64",
    "linux-x64",
    "linux-arm64",
    "osx-x64",
    "osx-arm64"
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
        Write-Host "Build failed for $rid (Exit Code: $($process.ExitCode)). Skipping." -ForegroundColor Red
        continue
    }

    Write-Host "Build successful for $rid." -ForegroundColor Green

    if (-not (Test-Path "./publish")) {
        New-Item -ItemType Directory -Path "./publish" | Out-Null
    }

    $exeFiles = Get-ChildItem -Path $outputDir -Filter "*.exe" -File
    if ($exeFiles.Count -gt 0) {
        foreach ($file in $exeFiles) {
            $destPath = Join-Path "./publish" $file.Name
            try {
                Move-Item -Path $file.FullName -Destination $destPath -Force
                Write-Host "Moved $($file.Name) to ./publish/" -ForegroundColor DarkGray
            }
            catch {
                Write-Warning "Failed to move $($file.Name): $_"
            }
        }
    } else {
        Write-Host "No .exe files found in $outputDir (expected for non-Windows RIDs)." -ForegroundColor Yellow
    }

    Write-Host "--- Done with $rid ---" -ForegroundColor Green
}