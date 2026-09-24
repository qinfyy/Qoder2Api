$json = @'
{
  "model": "auto",
  "stream": false,
  "messages": [
    { "role": "user", "content": "Hi" }
  ]
}
'@

try {
    $resp = Invoke-RestMethod -Uri "http://localhost:5166/v1/chat/completions" -Method Post -ContentType "application/json" -Body $json
    Write-Host "Response received:"
    $resp | ConvertTo-Json -Depth 5
} catch {
    Write-Error $_
}
