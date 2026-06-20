$runtimes = @(
    "win-x64",
    "win-arm64",
    "linux-x64",
    "linux-arm64",
    "osx-x64",
    "osx-arm64"
)

foreach ($rid in $runtimes) {

    $outputDir = "./publish/$rid"

    dotnet publish `
        -c Release `
        -f net10.0 `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=true `
        -o $outputDir

    # Generate checksums for all files in the publish folder
    Get-ChildItem $outputDir -File | ForEach-Object {

        $hash = Get-FileHash $_.FullName -Algorithm SHA256

        $checksumFile = "$($_.FullName).sha256"

        "$($hash.Hash)  $($_.Name)" |
            Out-File $checksumFile -Encoding ascii
    }
}