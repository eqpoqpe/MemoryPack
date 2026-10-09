param(
    [string]$VcVarsAll = '',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$artifactDirectory = Join-Path $repositoryRoot 'artifacts'
$outputDirectory = Join-Path $artifactDirectory "ZeroNativeAot/$runId"
$publishLog = Join-Path $artifactDirectory "native-aot-$runId.log"
$binlog = Join-Path $artifactDirectory "native-aot-$runId.binlog"
$runLog = Join-Path $artifactDirectory "native-aot-$runId-run.log"
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

$publishArguments = @('publish', (Join-Path $PSScriptRoot 'ZeroNativeAot.csproj'), '-c', 'Release',
    '-r', $RuntimeIdentifier, '-p:Platform=AnyCPU', '-o', $outputDirectory, "-bl:$binlog")
if ($VcVarsAll)
{
    if (!(Test-Path -LiteralPath $VcVarsAll -PathType Leaf)) { throw "VC environment script not found: $VcVarsAll" }
    $vcEnvironment = & cmd.exe /d /c "`"$VcVarsAll`" amd64 >nul && set"
    if ($LASTEXITCODE -ne 0) { throw 'VC tool environment initialization failed.' }
    foreach ($line in $vcEnvironment)
    {
        if ($line -match '^(PATH|INCLUDE|LIB|LIBPATH|VCToolsInstallDir|VCINSTALLDIR|WindowsSdkDir|WindowsSDKVersion|UniversalCRTSdkDir|UCRTVersion)=(.*)$')
        {
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
        }
    }
    $publishArguments += '-p:IlcUseEnvironmentalTools=true'
}

& dotnet @publishArguments 2>&1 | Tee-Object -FilePath $publishLog
$publishExit = $LASTEXITCODE
if ($publishExit -ne 0) { throw "Native publish failed (exit $publishExit). See $publishLog" }
if (!(Test-Path -LiteralPath $binlog)) { throw "Missing publish binlog: $binlog" }
if (Select-String -Path $publishLog -Pattern 'warning IL\d+' -Quiet)
{
    throw "Native publish emitted IL trimming/AOT warnings. See $publishLog"
}

$executableName = if ($RuntimeIdentifier.StartsWith('win-')) { 'ZeroNativeAot.exe' } else { 'ZeroNativeAot' }
$executable = Join-Path $outputDirectory $executableName
foreach ($mode in @('', '--deserialize-first-model', '--deserialize-first-union'))
{
    $nativeArguments = @('--require-aot')
    if ($mode) { $nativeArguments += $mode }
    & $executable @nativeArguments 2>&1 | Tee-Object -FilePath $runLog -Append
    $nativeExit = $LASTEXITCODE
    if ($nativeExit -ne 0) { throw "Native validation failed (exit $nativeExit). See $runLog" }
}
Write-Host "Verified zero IL warnings and native scenarios. Publish log: $publishLog; binlog: $binlog; run log: $runLog"
