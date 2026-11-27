$ErrorActionPreference = 'Stop'
$server = 'root@mdm-tsd'
$caConfig = $env:TSD_CA_CONFIG
$caChain = $env:TSD_CA_CHAIN
if ([string]::IsNullOrWhiteSpace($caConfig) -or [string]::IsNullOrWhiteSpace($caChain)) {
    throw 'Задайте TSD_CA_CONFIG и TSD_CA_CHAIN'
}
$template = 'WebServer'
$remote = '/opt/tsd-portal'
$work = Join-Path $env:TEMP 'tsd-portal-cert'
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Invoke-Remote([string]$command) {
    $output = & ssh -o BatchMode=yes -o ConnectTimeout=15 $server $command
    if ($LASTEXITCODE -ne 0) { throw "ssh failed: $command" }
    return $output
}

$meta = Invoke-Remote "openssl x509 -in $remote/certs/portal.crt -noout -enddate -issuer -subject"
$endLine = $meta | Where-Object { $_ -like 'notAfter=*' } | Select-Object -First 1
$issuer = ($meta | Where-Object { $_ -like 'issuer=*' } | Select-Object -First 1)
$subject = ($meta | Where-Object { $_ -like 'subject=*' } | Select-Object -First 1)
$rawDate = (($endLine -replace '^notAfter=', '').Trim() -replace '\s+', ' ')
$expires = [DateTime]::ParseExact($rawDate, 'MMM d HH:mm:ss yyyy GMT', [Globalization.CultureInfo]::InvariantCulture)
$days = ($expires.ToUniversalTime() - [DateTime]::UtcNow).TotalDays
$selfSigned = $issuer -eq ($subject -replace '^subject=', 'issuer=')
if (-not $selfSigned -and $days -gt 45) {
    Write-Output "certificate valid for $([int]$days) days"
    exit 0
}

Invoke-Remote "bash $remote/certs/make-csr.sh"
& scp -o BatchMode=yes "${server}:/tmp/portal.csr" (Join-Path $work 'portal.csr')
if ($LASTEXITCODE -ne 0) { throw 'could not download the request' }

$leaf = Join-Path $work 'portal.cer'
& certreq -submit -q -config $caConfig -attrib "CertificateTemplate:$template" (Join-Path $work 'portal.csr') $leaf
if ($LASTEXITCODE -ne 0) { throw "certificate request was not issued, exit $LASTEXITCODE" }

$chain = Join-Path $work 'chain.p7b'
& curl.exe -fsS -k --negotiate -u : -o $chain $caChain
if ($LASTEXITCODE -ne 0) { throw 'could not download the CA chain' }

& scp -o BatchMode=yes $leaf $chain "${server}:/tmp/"
if ($LASTEXITCODE -ne 0) { throw 'could not upload the certificate' }

Invoke-Remote @"
set -e
openssl pkcs7 -print_certs -in /tmp/chain.p7b -out /tmp/chain.pem
cat /tmp/portal.cer /tmp/chain.pem > /tmp/portal.full.crt
cp -a $remote/certs/portal.crt $remote/certs/portal.crt.bak
cp -a $remote/certs/portal.key $remote/certs/portal.key.bak
cat /tmp/portal.full.crt > $remote/certs/portal.crt
mv $remote/certs/portal.key.new $remote/certs/portal.key
chmod 644 $remote/certs/portal.crt $remote/certs/portal.key
cd $remote
docker compose exec -T nginx nginx -t
docker compose exec -T nginx nginx -s reload
docker compose restart portal
"@

Remove-Item $work -Recurse -Force
Write-Output 'portal certificate installed'
