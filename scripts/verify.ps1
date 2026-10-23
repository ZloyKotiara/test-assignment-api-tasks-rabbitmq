[CmdletBinding()]
param([switch]$SkipCompose)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    docker info --format '{{.ServerVersion}}'
    if ($LASTEXITCODE -ne 0) { throw 'Start Docker Desktop before verification.' }

    dotnet restore TaskApi.sln
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }

    dotnet build TaskApi.sln --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    dotnet test TaskApi.sln --no-build --no-restore --logger 'trx;LogFileName=all-tests.trx' --results-directory TestResults
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; see TestResults/all-tests.trx.' }

    if (-not $SkipCompose) {
        docker compose up --build -d
        if ($LASTEXITCODE -ne 0) { throw 'Compose startup failed.' }

        $apiPort = if ($env:API_PORT) { $env:API_PORT } else { '8080' }
        # Compose also accepts .env; read only the one non-secret value needed here.
        if (-not $env:API_PORT -and (Test-Path -LiteralPath '.env')) {
            $portLine = Get-Content -LiteralPath '.env' | Where-Object { $_ -match '^API_PORT=\d+$' } | Select-Object -Last 1
            if ($portLine) { $apiPort = $portLine.Split('=')[1] }
        }
        $healthUrl = 'http://localhost:' + $apiPort + '/health'
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try {
                $null = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2
                $ready = $true
                break
            }
            catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ready) { throw 'API did not become ready. Check docker compose logs api.' }
        docker compose ps
        Write-Output ('API ready: http://localhost:' + $apiPort)
    }
}
finally { Pop-Location }
