#Requires -Version 7
<#
.SYNOPSIS
    Regenerates the Go protocol code of the Fleeto agent from src/Fleetify.Protocol/Protos/agent.proto.

.DESCRIPTION
    Uses protoc from the Grpc.Tools NuGet package (already restored for Fleetify.Protocol) and protoc-gen-go,
    which is installed with `go install` when it is missing. The generated file is committed:
    agent/internal/protocol/agentv1/agent.pb.go. Run this after every change to agent.proto.
#>
[CmdletBinding()]
param(
    [string] $GrpcToolsVersion = '2.84.0',
    [string] $ProtocGenGoVersion = 'v1.36.12'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$agentDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $agentDir
$protoDir = Join-Path $repoRoot 'src/Fleetify.Protocol/Protos'
$outDir = Join-Path $agentDir 'internal/protocol/agentv1'

$goBin = 'C:\Program Files\Go\bin'
if (Test-Path $goBin) { $env:PATH = "$goBin;$env:PATH" }
$gopathBin = Join-Path (& go env GOPATH) 'bin'
$env:PATH = "$gopathBin;$env:PATH"

$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$toolsRoot = Join-Path $nuget "grpc.tools/$GrpcToolsVersion"
$protoc = Join-Path $toolsRoot 'tools/windows_x64/protoc.exe'
$include = Join-Path $toolsRoot 'build/native/include'
if (-not (Test-Path $protoc)) {
    throw "protoc was not found at $protoc. Restore src/Fleetify.Protocol first (dotnet restore) and run this script again."
}

if (-not (Get-Command protoc-gen-go -ErrorAction SilentlyContinue)) {
    Write-Host "Installing protoc-gen-go $ProtocGenGoVersion"
    & go install "google.golang.org/protobuf/cmd/protoc-gen-go@$ProtocGenGoVersion"
    if ($LASTEXITCODE -ne 0) { throw 'go install protoc-gen-go failed.' }
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# paths=source_relative writes agent.pb.go directly into $outDir, independent of go_package.
& $protoc "--proto_path=$protoDir" "--proto_path=$include" "--go_out=$outDir" '--go_opt=paths=source_relative' (Join-Path $protoDir 'agent.proto')
if ($LASTEXITCODE -ne 0) { throw 'protoc failed.' }

Write-Host "Generated $(Join-Path $outDir 'agent.pb.go')"
