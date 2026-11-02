# Builds tesseract.dll (Tesseract + Leptonica in one DLL, C API exported, static CRT) for win-x64 with MSVC.
#   powershell -File packaging/native/build-windows.ps1 [-OutDir artifacts/native/win-x64]
# No OpenMP, no libcurl, no libarchive, no image decoders: every one is switched off (and none is installed on the runner).
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\native\win-x64')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$versions = @{}
foreach ($line in Get-Content (Join-Path $PSScriptRoot 'versions.txt')) {
    if ($line -match '^([A-Z_]+)=(.*)$') { $versions[$Matches[1]] = $Matches[2] }
}

# Import the MSVC x64 environment (the runner has the Build Tools with the C++ workload, see CLAUDE.md, section CI).
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $install) { throw 'build-windows: no Visual Studio with the C++ tools found' }
$vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
cmd /c "`"$vcvars`" >nul && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) }
}

$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
$cmake = if ($cmakeCommand) { $cmakeCommand.Source } else { Join-Path $install 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' }
$ninjaCommand = Get-Command ninja -ErrorAction SilentlyContinue
$ninja = if ($ninjaCommand) { $ninjaCommand.Source } else { Join-Path $install 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe' }
if (-not (Test-Path $cmake) -or -not (Test-Path $ninja)) { throw 'build-windows: cmake or ninja not found' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('pagewing-native-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null

function Run($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "build-windows: $exe failed ($LASTEXITCODE)" }
}

function Fetch($url, $tag, $commit, $dir) {
    Run 'git' @('clone', '--quiet', '--depth', '1', '--branch', $tag, $url, $dir)
    $actual = (& git -C $dir rev-parse 'HEAD^{commit}').Trim()
    if ($actual -ne $commit) { throw "build-windows: $url $tag is $actual, expected $commit" }
}

try {
    Fetch $versions.LEPTONICA_URL $versions.LEPTONICA_TAG $versions.LEPTONICA_COMMIT "$work\leptonica"
    Fetch $versions.TESSERACT_URL $versions.TESSERACT_TAG $versions.TESSERACT_COMMIT "$work\tesseract"
    $prefix = "$work\prefix"
    $common = @('-DCMAKE_POLICY_DEFAULT_CMP0091=NEW', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded', '-DCMAKE_BUILD_TYPE=Release', '-GNinja', "-DCMAKE_MAKE_PROGRAM=$ninja")

    Run $cmake (@('-S', "$work\leptonica", '-B', "$work\lept-build", "-DCMAKE_INSTALL_PREFIX=$prefix", '-DBUILD_SHARED_LIBS=OFF', '-DBUILD_PROG=OFF', '-DSW_BUILD=OFF',
        '-DENABLE_ZLIB=OFF', '-DENABLE_PNG=OFF', '-DENABLE_GIF=OFF', '-DENABLE_JPEG=OFF', '-DENABLE_TIFF=OFF', '-DENABLE_WEBP=OFF', '-DENABLE_OPENJPEG=OFF') + $common)
    Run $cmake @('--build', "$work\lept-build", '--target', 'install')

    Run $cmake (@('-S', "$work\tesseract", '-B', "$work\tess-build", '-DBUILD_SHARED_LIBS=ON', '-DSW_BUILD=OFF', '-DOPENMP_BUILD=OFF', '-DDISABLE_CURL=ON', '-DDISABLE_ARCHIVE=ON',
        '-DBUILD_TRAINING_TOOLS=OFF', '-DBUILD_TESTS=OFF', '-DGRAPHICS_DISABLED=ON', '-DENABLE_LTO=OFF', "-DLeptonica_DIR=$prefix\lib\cmake\leptonica") + $common)
    Run $cmake @('--build', "$work\tess-build", '--target', 'libtesseract')

    # CMake names it tesseract<major><minor>.dll (tesseract55.dll) and puts it into bin\; the application loads it by path as tesseract.dll.
    $dll = Get-ChildItem "$work\tess-build" -Recurse -Filter 'tesseract*.dll' | Select-Object -First 1
    if (-not $dll) { throw 'build-windows: the DLL was not built' }
    Copy-Item $dll.FullName (Join-Path $OutDir 'tesseract.dll') -Force
    Write-Host "build-windows: $((Get-Item (Join-Path $OutDir 'tesseract.dll')).Length) bytes in $OutDir\tesseract.dll"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
