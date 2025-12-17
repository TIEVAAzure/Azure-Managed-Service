# Build and Deploy TIEVA Functions
Write-Host "Building..." -ForegroundColor Cyan
dotnet publish TIEVA_Functions.csproj -c Release -o ./publish

Write-Host "Deploying..." -ForegroundColor Cyan
Push-Location ./publish
$env:Path += ";$env:APPDATA\npm"
func azure functionapp publish func-tievaPortal-6612 --dotnet-isolated --no-build
Pop-Location

Write-Host "Done!" -ForegroundColor Green
