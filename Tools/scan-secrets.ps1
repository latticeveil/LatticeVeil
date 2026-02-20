param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$patterns = @(
    'SUPABASE_SERVICE_ROLE(_KEY)?\s*[:=]\s*["'']?[^"''\s]{8,}',
    'EOS_CLIENT_SECRET\s*[:=]\s*["'']?[^"''\s]{6,}',
    'GATE_ADMIN_TOKEN\s*[:=]\s*["'']?[^"''\s]{6,}',
    'JWT_SECRET\s*[:=]\s*["'']?[^"''\s]{6,}',
    'private_key\s*[:=]\s*["'']?[^"''\s]{8,}',
    'BEGIN PRIVATE KEY',
    'sb_secret_[A-Za-z0-9._-]+'
)

$excludePathRegex = '\\(\.git|\.vs|bin|obj|_temp|_tmp|temp|ThirdParty\\EOS\\SDK)\\'
$placeholderRegex = '(REPLACE_WITH|PUT_SECRET_HERE|<your-admin-token>|YOUR_SUPABASE_PROJECT|YOUR_SITE_DOMAIN)'

Write-Host "Scanning for potential secret leaks in $Root ..."

if (Get-Command rg -ErrorAction SilentlyContinue) {
    $rgArgs = @(
        '--hidden',
        '--line-number',
        '--color',
        'never',
        '--no-heading',
        '--glob',
        '!.git/**',
        '--glob',
        '!**/bin/**',
        '--glob',
        '!**/obj/**',
        '--glob',
        '!**/.vs/**',
        '--glob',
        '!ThirdParty/EOS/SDK/**'
    )

    foreach ($pattern in $patterns) {
        $rgArgs += @('-e', $pattern)
    }
    $rgArgs += $Root

    $rgOutput = & rg @rgArgs
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        $filtered = $rgOutput | Where-Object {
            $_ -notmatch '\\Tools\\scan-secrets\.ps1:' -and
            $_ -notmatch '\.example\.env:' -and
            $_ -notmatch '\.template\.json:' -and
            $_ -notmatch $placeholderRegex
        }

        if ($filtered.Count -gt 0) {
            $filtered | ForEach-Object { Write-Output $_ }
            Write-Host ""
            Write-Host "Potential secret-like values found. Review before commit." -ForegroundColor Red
            exit 1
        }

        Write-Host "No potential secrets found."
        exit 0
    }

    if ($exitCode -eq 1) {
        Write-Host "No potential secrets found."
        exit 0
    }

    exit $exitCode
}

$hits = @()
$files = Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notmatch $excludePathRegex
    }

foreach ($pattern in $patterns) {
    $matches = $files | Select-String -Pattern $pattern -ErrorAction SilentlyContinue
    if ($matches) {
        $hits += $matches
    }
}

if ($hits.Count -gt 0) {
    $filteredHits = $hits | Where-Object {
        $_.Path -notmatch 'Tools\\scan-secrets\.ps1$' -and
        $_.Path -notmatch '\.example\.env$' -and
        $_.Path -notmatch '\.template\.json$' -and
        $_.Line -notmatch $placeholderRegex
    }

    if ($filteredHits.Count -eq 0) {
        Write-Host "No potential secrets found."
        exit 0
    }

    $filteredHits | ForEach-Object { "{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim() } | Sort-Object -Unique
    Write-Host ""
    Write-Host "Potential secret-like values found. Review before commit." -ForegroundColor Red
    exit 1
}

Write-Host "No potential secrets found."
exit 0
