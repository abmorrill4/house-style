param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force -Settings @{ Role = 1 }
Set-Content "E:\HouseStyle\e2e\hold-ready.txt" $run.Project
Wait-Until { Test-Path "E:\HouseStyle\e2e\hold-stop.txt" } 3000 "the stop flag" | Out-Null
