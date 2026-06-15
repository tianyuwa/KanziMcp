$ErrorActionPreference = "Continue"
$LOG_FILE = "C:\KanziMonitor\monitor.log"
$MD5_FILE = "C:\KanziMonitor\build_md5.txt"
$feishuWebhook = "https://open.feishu.cn/open-apis/bot/v2/hook/0668f305-44e3-4a03-9b7a-f8ea8b8fcce8"

$logDir = Split-Path $LOG_FILE -Parent
if (!(Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

function Write-Log {
    param($Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logMessage = "$timestamp - $Message"
    Write-Host $logMessage
    Add-Content -Path $LOG_FILE -Value $logMessage -Encoding UTF8
}

function Send-FeishuMessage {
    param([string]$Title, [string]$Text, [string]$Color = "red")
    if ([string]::IsNullOrEmpty($feishuWebhook)) { return }
    $template = if ($Color -eq "red") { "red" } else { "green" }
    $body = @{
        msg_type = "interactive"
        card = @{
            config = @{ wide_screen_mode = $true }
            header = @{
                title = @{ tag = "plain_text"; content = $Title }
                template = $template
            }
            elements = @( @{ tag = "div"; text = @{ tag = "lark_md"; content = $Text } } )
        }
    }
    # Use UTF-8 encoding with compress to avoid Chinese garbled text
    $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 10))
    try {
        Invoke-RestMethod -Uri $feishuWebhook -Method Post -Body $jsonBytes -ContentType "application/json; charset=utf-8" -ErrorAction Stop | Out-Null
        Write-Log "Feishu sent: $Title"
    } catch {
        Write-Log "Feishu failed: $_"
    }
}

function Get-RemoteMD5 {
    param([string]$OssDir)
    $ossutil = "E:\wangtianyu\ossutil-v1.7.19-windows-amd64\ossutil64.exe"
    & $ossutil stat "$OssDir" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return $null }
    & $ossutil cp --recursive "$OssDir" "$env:TEMP\oss_check_$PID\" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return $null }
    $files = Get-ChildItem -Path "$env:TEMP\oss_check_$PID\" -Recurse -File
    $hashes = @()
    foreach ($f in $files) {
        $hash = (Get-FileHash $f.FullName -Algorithm MD5).Hash
        $relPath = $f.FullName.Replace("$env:TEMP\oss_check_$PID\", "")
        $hashes += "$hash  $relPath"
    }
    $combinedInput = ($hashes | Sort-Object) -join "`n"
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($combinedInput)
    $hashBytes = $md5.ComputeHash($bytes)
    $combinedHash = [BitConverter]::ToString($hashBytes) -replace '-', ''
    Remove-Item -Path "$env:TEMP\oss_check_$PID\" -Recurse -Force -ErrorAction SilentlyContinue
    return $combinedHash
}

function Get-LocalMD5 {
    param([string]$LocalDir)
    if (!(Test-Path $LocalDir)) { return $null }
    $files = Get-ChildItem -Path $LocalDir -Recurse -File
    $hashes = @()
    foreach ($f in $files) {
        $hash = (Get-FileHash $f.FullName -Algorithm MD5).Hash
        $relPath = $f.FullName.Replace($LocalDir, "")
        $hashes += "$hash  $relPath"
    }
    if ($hashes.Count -eq 0) { return $null }
    $combinedInput = ($hashes | Sort-Object) -join "`n"
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($combinedInput)
    $hashBytes = $md5.ComputeHash($bytes)
    $combinedHash = [BitConverter]::ToString($hashBytes) -replace '-', ''
    return $combinedHash
}

function Stop-KanziStudio {
    param([int]$MaxWaitSeconds = 10)
    $kanziProcesses = Get-Process -Name "KanziStudio" -ErrorAction SilentlyContinue
    if ($kanziProcesses) {
        Write-Log "Closing Kanzi Studio processes..."
        # Try graceful close first
        $kanziProcesses | ForEach-Object { $_.CloseMainWindow() | Out-Null }
        for ($i = 0; $i -lt $MaxWaitSeconds; $i++) {
            Start-Sleep -Seconds 1
            $remaining = Get-Process -Name "KanziStudio" -ErrorAction SilentlyContinue
            if (-not $remaining) {
                Write-Log "Kanzi Studio closed gracefully."
                Start-Sleep -Seconds 2
                return $true
            }
            Write-Log "Waiting for Kanzi Studio to exit... ($($i + 1)/$MaxWaitSeconds)"
        }
        # Force kill if still running (e.g., unsaved project dialog)
        $remaining = Get-Process -Name "KanziStudio" -ErrorAction SilentlyContinue
        if ($remaining) {
            Write-Log "Force killing Kanzi Studio..."
            $remaining | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
        }
        return -not (Get-Process -Name "KanziStudio" -ErrorAction SilentlyContinue)
    }
    Write-Log "Kanzi Studio not running."
    return $true
}

$ossutil = "E:\wangtianyu\ossutil-v1.7.19-windows-amd64\ossutil64.exe"
$flagFile = "oss://mcpkanzipublish/incoming/latest_build.txt"
$localFlag = "C:\KanziMonitor\last_build_time.txt"
$remoteDir = "oss://mcpkanzipublish/incoming/Build_MCP/"
$localDir = "C:\KanziMonitor\Build_MCP"

$lastTimestamp = $null
if (Test-Path $localFlag) { $lastTimestamp = Get-Content $localFlag -Raw }
$lastMD5 = $null
if (Test-Path $MD5_FILE) { $lastMD5 = Get-Content $MD5_FILE -Raw }

Write-Log "========== Monitor Started =========="

while ($true) {
    try {
        Write-Log "Checking for new build flag..."
        $statOut = (& $ossutil stat $flagFile 2>&1) -join "`n"

        if ($LASTEXITCODE -eq 0) {
            if ($statOut -match 'Last-Modified\s+:\s+(.+)') {
                $currentTimestamp = $matches[1].Trim()
                Write-Log "Remote flag timestamp: $currentTimestamp"

                if ($currentTimestamp -ne $lastTimestamp) {
                    Write-Log "Flag changed, checking if build content changed..."
                    Send-FeishuMessage -Title "New build detected" -Text "Timestamp: $currentTimestamp" -Color "blue"

                    $remoteMD5 = Get-RemoteMD5 -OssDir $remoteDir
                    $localMD5 = Get-LocalMD5 -LocalDir $localDir
                    Write-Log "Remote MD5: $remoteMD5, Local MD5: $localMD5"

                    $needDownload = $false
                    if ($null -eq $localMD5 -or $remoteMD5 -ne $localMD5) {
                        $needDownload = $true
                        Write-Log "Build changed, downloading..."
                    } else {
                        Write-Log "Build unchanged, skipping download."
                    }

                    if ($needDownload) {
                        if (!(Test-Path $localDir)) { New-Item -ItemType Directory -Path $localDir -Force | Out-Null }
                        & $ossutil cp --recursive $remoteDir "$localDir\" --force
                        if ($LASTEXITCODE -eq 0) {
                            Write-Log "Build downloaded."
                        } else {
                            Write-Log "Download failed"
                            Send-FeishuMessage -Title "Download Failed" -Text "Failed to download build from OSS" -Color "red"
                            Start-Sleep -Seconds 45
                            continue
                        }
                    }

                    $pluginSrc = "$localDir\KanziMcpPlugin\kanzi3.9.10\PluginKanziMCP.dll"
                    $pluginDest = "C:\ProgramData\Rightware\Kanzi 3.9.10\plugins\PluginKanziMCP.dll"
                    if (Test-Path $pluginSrc) {
                        $closed = Stop-KanziStudio
                        if (-not $closed) {
                            Write-Log "Warning: Could not close Kanzi Studio. Plugin copy may fail."
                            Send-FeishuMessage -Title "Warning" -Text "Could not close Kanzi Studio. DLL may be locked." -Color "yellow"
                        }
                        $copySuccess = $false
                        for ($retry = 1; $retry -le 3; $retry++) {
                            try {
                                Copy-Item $pluginSrc $pluginDest -Force -ErrorAction Stop
                                Write-Log "Plugin copied successfully."
                                $copySuccess = $true
                                break
                            } catch {
                                Write-Log "Plugin copy attempt $retry failed: $_"
                                if ($retry -lt 3) {
                                    Start-Sleep -Seconds 2
                                    Stop-KanziStudio
                                }
                            }
                        }
                        if (-not $copySuccess) {
                            Write-Log "Plugin copy failed after retries."
                            Send-FeishuMessage -Title "Plugin Copy Failed" -Text "Could not update plugin DLL. Check Kanzi Studio is closed." -Color "red"
                        }
                    } else {
                        Write-Log "Plugin source not found: $pluginSrc"
                    }

                    $openProjPath = "E:\wangtianyu\kanziMCP3910\Untitled\Tool_project\openProj.bat"
                    if (Test-Path $openProjPath) {
                        Write-Log "Starting Kanzi Studio..."
                        # Use Start-Process instead of Invoke-Item to avoid shell hang
                        $procKanzi = Start-Process -FilePath $openProjPath -PassThru -WindowStyle Normal
                        Write-Log "Kanzi Studio process started (PID: $($procKanzi.Id))"
                        Start-Sleep -Seconds 60
                    }

                    $serverExe = "$localDir\KanziMcpServer\KanziMcpServer.exe"
                    $mainExe = "$localDir\main.exe"
                    $testResultFile = "C:\KanziMonitor\test_result_latest.txt"

                    if (Test-Path $mainExe) {
                        $exeToRun = $mainExe
                        Write-Log "Running test (main.exe)..."
                    } elseif (Test-Path "$localDir\test_mcp_client.py") {
                        $exeToRun = $localDir + "\test_mcp_client.py"
                        Write-Log "Running test (python)..."
                    } else {
                        Write-Log "test script not found."
                        $exeToRun = $null
                    }

                    if ($null -ne $exeToRun) {
                        $isPython = $exeToRun -like "*.py"
                        $psi = New-Object System.Diagnostics.ProcessStartInfo
                        if ($isPython) {
                            $psi.FileName = "python"
                            $psi.Arguments = "`"$exeToRun`" --server `"$serverExe`" --auto"
                        } else {
                            $psi.FileName = $exeToRun
                            $psi.Arguments = "--server `"$serverExe`" --auto"
                        }
                        $psi.UseShellExecute = $false
                        $psi.RedirectStandardInput = $true
                        $psi.RedirectStandardOutput = $true
                        $psi.RedirectStandardError = $true
                        $psi.CreateNoWindow = $true
                        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
                        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

                        $proc = New-Object System.Diagnostics.Process
                        $proc.StartInfo = $psi
                        Write-Log "Starting test process..."
                        $proc.Start() | Out-Null

                        # Use Task.Wait() instead of Process.WaitForExit() to avoid deadlock on Kill+ReadToEnd
                        $exitTask = [System.Threading.Tasks.Task]::Run([Action]{ $proc.WaitForExit() })
                        $waitOk = $exitTask.Wait(180000)

                        $testTimedOut = $false
                        if ($waitOk) {
                            Write-Log "Test process exited with code: $($proc.ExitCode)"
                            $stdout = $proc.StandardOutput.ReadToEnd()
                            $stderr = $proc.StandardError.ReadToEnd()
                            $stdout | Out-File $testResultFile
                            if ($stderr) { Write-Log "Test stderr: $stderr" }
                        } else {
                            # Timeout - kill immediately, do NOT read streams (avoids deadlock)
                            Write-Log "Test process timed out (180s), killing..."
                            try { $proc.Kill($true) } catch { }
                            Start-Sleep -Seconds 2
                            @"
========================================
TEST TIMEOUT: Process exceeded 180 second limit
Killed at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
========================================
"@ | Out-File $testResultFile
                            $testTimedOut = $true
                            Send-FeishuMessage -Title "Test Timeout" -Text "Test process timed out after 180s" -Color "red"
                        }

                        $proc.Dispose()
                        Write-Log "Test done. Output saved to $testResultFile"

                        # Upload result file (even on timeout)
                        if (Test-Path $testResultFile) {
                            & $ossutil cp $testResultFile "oss://mcpkanzipublish/outgoing/result_latest.txt" --force
                            Write-Log "Result uploaded."
                        }

                        # Upload diagnostic bundle (always, regardless of timeout)
                        $diagDir = "C:\temp"
                        $diagOutput = "C:\KanziMonitor\diagnostic_bundle.zip"
                        if (Test-Path $diagOutput) { Remove-Item $diagOutput -Force }
                        if (Test-Path $diagDir) {
                            $diagFiles = @()
                            if (Test-Path "$diagDir\KanziApiDump.txt") { $diagFiles += "$diagDir\KanziApiDump.txt" }
                            if (Test-Path "$diagDir\KanziMcpPlugin.log") { $diagFiles += "$diagDir\KanziMcpPlugin.log" }
                            if (Test-Path $testResultFile) { $diagFiles += $testResultFile }
                            if ($diagFiles.Count -gt 0) {
                                $tempDir = "$env:TEMP\diag_bundle_$PID"
                                New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
                                Copy-Item $diagFiles $tempDir -Force
                                Compress-Archive -Path "$tempDir\*" -DestinationPath $diagOutput -Force
                                Remove-Item $tempDir -Recurse -Force
                                & $ossutil cp $diagOutput "oss://mcpkanzipublish/outgoing/diagnostic_bundle.zip" --force
                                Write-Log "Diagnostic bundle uploaded ($($diagFiles.Count) files)."
                            }
                        }

                        # Only evaluate pass/fail if test completed (not timed out)
                        if (-not $testTimedOut -and (Test-Path $testResultFile)) {
                            $resultContent = Get-Content $testResultFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                            $testPassed = $resultContent -match "TEST_RESULT:\s*PASS"
                            $coreMatch = $resultContent -match "Core Tests:\s*(\d+)/(\d+)"
                            $kanziMatch = $resultContent -match "Kanzi Tests:\s*(\d+)/(\d+)"
                            $corePassed = if ($coreMatch) { $matches[1] } else { 0 }
                            $coreTotal = if ($coreMatch) { $matches[2] } else { 0 }
                            $kanziPassed = if ($kanziMatch) { $matches[1] } else { 0 }
                            $kanziTotal = if ($kanziMatch) { $matches[2] } else { 0 }

                            if ($testPassed) {
                                $msg = "MCP Server core tests passed ($corePassed/$coreTotal)."
                                if ($kanziPassed -lt $kanziTotal) {
                                    $msg += " Kanzi tests: $kanziPassed/$kanziTotal (requires Kanzi Studio running)."
                                }
                                Send-FeishuMessage -Title "[PASS] Test Succeeded" -Text $msg -Color "green"
                            } else {
                                $msg = "MCP Server core tests failed ($corePassed/$coreTotal). Check logs. MD5: $remoteMD5"
                                Send-FeishuMessage -Title "[FAIL] Test Failed" -Text $msg -Color "red"
                            }
                        }
                    } else {
                        Write-Log "Test script (main.exe / test_mcp_client.py) not found."
                    }

                    $remoteMD5 | Out-File $MD5_FILE -Encoding UTF8
                    $currentTimestamp | Out-File $localFlag -Encoding UTF8
                    $lastTimestamp = $currentTimestamp
                    $lastMD5 = $remoteMD5
                } else {
                    Write-Log "No new build."
                }
            } else {
                Write-Log "Failed to parse timestamp."
            }
        } else {
            Write-Log "Flag not ready (exit: $LASTEXITCODE)"
        }
    } catch {
        Write-Log "Error: $_"
        Send-FeishuMessage -Title "Monitor Error" -Text "Script error: $_" -Color "red"
    }

    Start-Sleep -Seconds 45
}
