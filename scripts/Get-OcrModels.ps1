$ErrorActionPreference = 'Stop'

$models = Join-Path $PSScriptRoot '../src/Snapaste.App/Ocr/Models'
$runtime = Join-Path $PSScriptRoot '../src/Snapaste.App/Ocr/Runtime'
$release = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0'
$files = @(
    @{
        Name = 'PP-OCRv6_det_small.onnx'
        Path = 'onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx'
        SHA256 = '090F04ABCD9D9A7498BC4EBF677E4CB9BDCE1FE4197DDB7E529F1EF44E1FF94F'
    }
    @{
        Name = 'PP-OCRv6_rec_small.onnx'
        Path = 'onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx'
        SHA256 = '6F327246B50388F3C176AE304BD95767EA6DC0C9AE92153EF8CBE210B3C14884'
    }
    @{
        Name = 'ppocrv6_small_dict.txt'
        Path = 'paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt'
        SHA256 = 'B5F2BFE2BDD9448429E3E82B51C789775D9B42F2403D082B00662EB77E401C5D'
    }
    @{
        Name = 'ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'
        Path = 'onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'
        SHA256 = '54379AE5174D026780215FC748A7F31910DEE36818E63D49E17DC598ECC82DF7'
    }
)

function Test-Asset([string] $Path, [string] $SHA256) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $SHA256
}

function Save-VerifiedDownload([string] $Url, [string] $Destination, [string] $SHA256) {
    if (Test-Asset $Destination $SHA256) { return }
    $partial = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
    try {
        Write-Host "Downloading $([IO.Path]::GetFileName($Url))"
        & curl.exe --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --connect-timeout 15 --max-time 240 --output $partial $Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed for $Url" }
        if (!(Test-Asset $partial $SHA256)) { throw "SHA-256 verification failed for $Url" }
        Move-Item -LiteralPath $partial -Destination $Destination -Force
    }
    finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial }
    }
}

[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($models)) | Out-Null
foreach ($file in $files) {
    Save-VerifiedDownload "$release/$($file.Path)" (Join-Path $models $file.Name) $file.SHA256
}

# ONNX Runtime is built against a newer Visual C++ runtime than some Windows 11 installations provide.
$runtimeFiles = [ordered]@{
    'vcruntime140.dll'   = 'D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7'
    'vcruntime140_1.dll' = 'A7146C08F89FE5B04541AB507CDB59FF7B44534D4BA3C668A426C6450A03434E'
    'msvcp140.dll'       = '7C26614E1D733892C2DEAC7E245CE115504B1D80592DD0A01B08E3E5A55F89CA'
    'msvcp140_1.dll'     = '206C931BF90FDAD8816DE3B5E2EF80B2BCAA9406C89ECC05FE6FDDFFE251E982'
}
$missing = @($runtimeFiles.Keys | Where-Object { !(Test-Asset (Join-Path $runtime $_) $runtimeFiles[$_]) })
if ($missing.Count -gt 0) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($runtime)) | Out-Null
    $package = Join-Path $runtime ('.' + [Guid]::NewGuid().ToString('N') + '.partial')
    $hash = '24A1C8DBA0376D9B008AC2BA430D13C06664FC6F163511FD7866485030E289AF'
    $packageUrl = 'https://download.visualstudio.microsoft.com/download/pr/3984a6ef-2ffc-4c07-962e-bf9e9b862819/24a1c8dba0376d9b008ac2ba430d13c06664fc6f163511fd7866485030e289af/Microsoft.VC.14.51.CRT.Redist.X64.base.vsix'
    try {
        Save-VerifiedDownload $packageUrl $package $hash
        $zip = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            foreach ($name in $missing) {
                $entry = $zip.GetEntry("Contents/VC/Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT/$name")
                if ($null -eq $entry) { throw "Visual C++ Runtime package does not contain $name" }
                $partial = Join-Path $runtime ('.' + [Guid]::NewGuid().ToString('N') + '.partial')
                try {
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $partial)
                    if (!(Test-Asset $partial $runtimeFiles[$name])) { throw "SHA-256 verification failed for $name" }
                    Move-Item -LiteralPath $partial -Destination (Join-Path $runtime $name) -Force
                }
                finally { if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial } }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { if (Test-Path -LiteralPath $package -PathType Leaf) { Remove-Item -LiteralPath $package } }
}
