<#
.SYNOPSIS
    Dead Man's Switch - recurring, extendable shutdown timer (WPF + animation clock).
.DESCRIPTION
    The bars are driven by WPF DoubleAnimations (the render thread animates the
    Width itself) rather than per-tick code, so the motion does not depend on the
    script forcing redraws. The timer only updates the countdown text and handles
    the phase logic.
.NOTES
    Run:   powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File DeadMansSwitch.ps1
    Build: .\build\Build.ps1   (wraps Invoke-ps2exe and stamps the version)
#>
# ===================== CONFIG =====================
# Default timer lengths; the values set in the window are saved to
# $SettingsFile and take over from these on the next start.
$ExtendSeconds = 300
$WarnSeconds   = 60
$SettingsFile  = Join-Path $env:APPDATA 'DeadMansSwitch\settings.json'
$TickMs        = 250
$AllowCancel   = $true
$PlaySound     = $true
$ShutdownExe   = "shutdown.exe"
$ShutdownArgs  = "/s /f /t 0"   # /s shutdown | /r restart | /h hibernate
# ----- version / updates -----
$AppVersion      = '1.1'                                # managed by tools\Bump-Version.ps1
$CheckForUpdates = $true
$UpdateRepo      = 'samschultzponsys/dead-mans-switch'  # GitHub owner/repo
$UpdateAssetName = 'DeadMansSwitch.exe'               # release asset to download
# ==================================================

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SGfg {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
}
"@

# ----- saved settings -----
$MinCheckMinutes = 1;  $MaxCheckMinutes = 1440   # 1 minute .. 24 hours
$MinWarnSeconds  = 10; $MaxWarnSeconds  = 600

function Read-Settings {
    try {
        $s = Get-Content $SettingsFile -Raw -ErrorAction Stop | ConvertFrom-Json
        $m = [int]$s.CheckMinutes; $w = [int]$s.WarnSeconds
        if ($m -ge $MinCheckMinutes -and $m -le $MaxCheckMinutes) { $script:ExtendSeconds = $m * 60 }
        if ($w -ge $MinWarnSeconds  -and $w -le $MaxWarnSeconds)  { $script:WarnSeconds   = $w }
    } catch { }   # no file yet or unreadable: keep the defaults
}

function Save-Settings {
    New-Item -ItemType Directory -Force -Path (Split-Path $SettingsFile) | Out-Null
    [pscustomobject]@{ CheckMinutes = [int]($ExtendSeconds / 60); WarnSeconds = $WarnSeconds } |
        ConvertTo-Json | Set-Content -Path $SettingsFile -Encoding UTF8
}

Read-Settings

# ----- updater -----
# Asks GitHub for the latest release; if its tag is newer than $AppVersion and it
# carries $UpdateAssetName, offers to install it. Any failure is silent so a
# network problem never stops the timer from starting.
function Get-UpdateInfo {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$UpdateRepo/releases/latest" `
                                 -Headers @{ 'User-Agent' = 'DeadMansSwitch-Updater' } -TimeoutSec 8
        $latest = [version]($rel.tag_name.TrimStart('v','V'))
        if ($latest -le [version]$AppVersion) { return $null }
        $asset = $rel.assets | Where-Object { $_.name -eq $UpdateAssetName } | Select-Object -First 1
        if (-not $asset) { return $null }
        [pscustomobject]@{ Version = "$($latest.Major).$($latest.Minor)"; Url = $asset.browser_download_url }
    } catch { $null }
}

# A running exe can't overwrite itself, so download beside it and hand the swap
# to a small batch file that waits for this process to exit, replaces the exe
# and relaunches it.
function Install-Update($update) {
    $self = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    if ($self -match '(?i)\\(powershell|pwsh)\.exe$') {
        # Running as a plain script: nothing to swap, just point at the download.
        [System.Windows.MessageBox]::Show("Version $($update.Version) is available:`n$($update.Url)",
                                          "Dead Man's Switch") | Out-Null
        return $false
    }
    $new = "$self.new"
    Invoke-WebRequest -Uri $update.Url -OutFile $new -UseBasicParsing -TimeoutSec 60
    $bat = Join-Path $env:TEMP "DeadMansSwitch-update-$PID.cmd"
    @"
@echo off
:wait
tasklist /fi "PID eq $PID" | find "$PID" >nul && (timeout /t 1 /nobreak >nul & goto wait)
move /y "$new" "$self" >nul
start "" "$self"
del "%~f0"
"@ | Set-Content -Path $bat -Encoding ASCII
    Start-Process -FilePath $bat -WindowStyle Hidden
    return $true
}

if ($CheckForUpdates) {
    $update = Get-UpdateInfo
    if ($update) {
        $answer = [System.Windows.MessageBox]::Show(
            "Dead Man's Switch $($update.Version) is available (you have $AppVersion).`n`nUpdate now?",
            "Dead Man's Switch update", 'YesNo', 'Information')
        if ($answer -eq 'Yes') {
            try { if (Install-Update $update) { exit } } catch {
                [System.Windows.MessageBox]::Show("Update failed: $($_.Exception.Message)", "Dead Man's Switch") | Out-Null
            }
        }
    }
}

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Dead Man's Switch" Width="480" Height="390"
        WindowStartupLocation="CenterScreen" Topmost="True"
        ResizeMode="NoResize" Background="#FF1E2026">
  <StackPanel Margin="16">
    <TextBlock x:Name="Title" Text="Automatic Shutdown Scheduled"
               FontFamily="Segoe UI" FontSize="18" FontWeight="Bold"
               Foreground="#FFEBEBEB" HorizontalAlignment="Center" Margin="0,2,0,2"/>
    <TextBlock x:Name="Status" Text="" FontFamily="Segoe UI" FontSize="14"
               Foreground="#FFEBEBEB" HorizontalAlignment="Center" Margin="0,2,0,10"/>

    <TextBlock x:Name="ExtendLabel" Text="Time until next check"
               FontFamily="Segoe UI" FontSize="12" Foreground="#FFEBEBEB" Margin="0,0,0,3"/>
    <Border Background="#FF373A42" Height="24" Width="432" HorizontalAlignment="Left" CornerRadius="3">
      <Border x:Name="ExtendFill" Background="#FF2ECC71" Width="432" HorizontalAlignment="Left"/>
    </Border>

    <TextBlock x:Name="WarnLabel" Text="Final-warning countdown (idle)"
               FontFamily="Segoe UI" FontSize="12" Foreground="#FFEBEBEB" Margin="0,12,0,3"/>
    <Border Background="#FF373A42" Height="24" Width="432" HorizontalAlignment="Left" CornerRadius="3">
      <Border x:Name="WarnFill" Background="#FF787D87" Width="432" HorizontalAlignment="Left"/>
    </Border>

    <Button x:Name="ExtendBtn" Content="I'm here" Height="42" Margin="0,16,0,8"
            Background="#FF27AE60" Foreground="White" FontSize="14" FontWeight="Bold" BorderThickness="0"/>
    <Button x:Name="CancelBtn" Content="Cancel shutdown timer" Height="28"
            Background="#FF1E2026" Foreground="#FFEBEBEB" FontSize="12"/>

    <StackPanel Orientation="Horizontal" Margin="0,14,0,0">
      <TextBlock Text="Check every" FontFamily="Segoe UI" FontSize="12" Foreground="#FFEBEBEB" VerticalAlignment="Center"/>
      <TextBox x:Name="CheckMinBox" Width="44" Margin="6,0,4,0" FontSize="12" TextAlignment="Center"/>
      <TextBlock Text="min" FontFamily="Segoe UI" FontSize="12" Foreground="#FFEBEBEB" VerticalAlignment="Center"/>
      <TextBlock Text="Final warning" FontFamily="Segoe UI" FontSize="12" Foreground="#FFEBEBEB"
                 VerticalAlignment="Center" Margin="18,0,0,0"/>
      <TextBox x:Name="WarnSecBox" Width="44" Margin="6,0,4,0" FontSize="12" TextAlignment="Center"/>
      <TextBlock Text="sec" FontFamily="Segoe UI" FontSize="12" Foreground="#FFEBEBEB" VerticalAlignment="Center"/>
      <Button x:Name="SaveBtn" Content="Save" Width="60" Margin="18,0,0,0" FontSize="12"/>
    </StackPanel>
    <TextBlock x:Name="SettingsMsg" Text="" FontFamily="Segoe UI" FontSize="11" Foreground="#FFB0B4BC" Margin="0,4,0,0"/>
  </StackPanel>
</Window>
"@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$win = [System.Windows.Markup.XamlReader]::Load($reader)
$win.Title = "Dead Man's Switch v$AppVersion"

$Title=$win.FindName("Title"); $Status=$win.FindName("Status")
$ExtendFill=$win.FindName("ExtendFill"); $WarnFill=$win.FindName("WarnFill")
$WarnLabel=$win.FindName("WarnLabel")
$ExtendBtn=$win.FindName("ExtendBtn"); $CancelBtn=$win.FindName("CancelBtn")
$CheckMinBox=$win.FindName("CheckMinBox"); $WarnSecBox=$win.FindName("WarnSecBox")
$SaveBtn=$win.FindName("SaveBtn"); $SettingsMsg=$win.FindName("SettingsMsg")
if (-not $AllowCancel) { $CancelBtn.Visibility = 'Collapsed' }

function Show-Settings {
    $CheckMinBox.Text = [int]($ExtendSeconds / 60); $WarnSecBox.Text = $WarnSeconds
    $ExtendBtn.Content = "I'm here  -  reset to $([int]($ExtendSeconds / 60)) min"
}

$TrackWidth = 432.0
function NewBrush([int]$r,[int]$g,[int]$b){ New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb($r,$g,$b)) }
$Green=NewBrush 46 204 113; $Red=NewBrush 231 76 60; $Grey=NewBrush 120 125 135
$BgNormal=NewBrush 30 32 38; $BgWarn=NewBrush 70 22 22
$WidthProp = [System.Windows.FrameworkElement]::WidthProperty

function Fmt([double]$s){ $s=[int][math]::Ceiling($s); if($s -lt 0){$s=0}; '{0}:{1:00}' -f [int]($s/60), ($s%60) }

# Hand the bar's motion to WPF's animation clock.
function Animate-Bar($fill, [double]$seconds){
    $a = New-Object System.Windows.Media.Animation.DoubleAnimation
    $a.From = $TrackWidth
    $a.To   = 0.0
    $a.Duration = New-Object System.Windows.Duration ([TimeSpan]::FromSeconds($seconds))
    $fill.BeginAnimation($WidthProp, $a)
}
function Reset-Bar($fill){
    $fill.BeginAnimation($WidthProp, $null)   # detach animation, restore local value
    $fill.Width = $TrackWidth
}

function Force-Front {
    $h = (New-Object System.Windows.Interop.WindowInteropHelper $win).Handle
    [SGfg]::keybd_event(0x12,0,0,[UIntPtr]::Zero); [SGfg]::keybd_event(0x12,0,2,[UIntPtr]::Zero)
    [SGfg]::SetForegroundWindow($h) | Out-Null
    $win.Topmost=$false; $win.Topmost=$true; $win.Activate() | Out-Null
}

# ----- state -----
$script:phase='extend'; $script:deadline=(Get-Date).AddSeconds($ExtendSeconds)
$script:phaseTotal=$ExtendSeconds; $script:reallyClose=$false; $script:warnTicks=0

function Enter-Warn {
    $script:phase='warn'; $script:deadline=(Get-Date).AddSeconds($WarnSeconds)
    $script:phaseTotal=$WarnSeconds; $script:warnTicks=0
    $win.Background=$BgWarn; $Title.Text="SHUTTING DOWN"
    $WarnFill.Background=$Red
    Animate-Bar $WarnFill $WarnSeconds
    if($PlaySound){ [System.Media.SystemSounds]::Exclamation.Play() }
    Force-Front
}
function Invoke-Extend {
    $script:phase='extend'; $script:deadline=(Get-Date).AddSeconds($ExtendSeconds)
    $script:phaseTotal=$ExtendSeconds
    $win.Background=$BgNormal; $Title.Text="Automatic Shutdown Scheduled"
    $WarnLabel.Text="Final-warning countdown (idle)"
    $ExtendFill.Background=$Green
    Animate-Bar $ExtendFill $ExtendSeconds
    Reset-Bar $WarnFill; $WarnFill.Background=$Grey
}
function Invoke-Shutdown {
    $timer.Stop(); $script:reallyClose=$true
    Start-Process -FilePath $ShutdownExe -ArgumentList $ShutdownArgs -WindowStyle Hidden
    $win.Close()
}

# ----- timer drives ONLY text + phase logic -----
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval=[TimeSpan]::FromMilliseconds($TickMs)
$timer.Add_Tick({
    $remaining = ($script:deadline-(Get-Date)).TotalSeconds
    if($script:phase -eq 'extend'){
        $Status.Text="Next check in "+(Fmt $remaining)
        if($remaining -le 0){ Enter-Warn }
    } else {
        $WarnLabel.Text="FINAL WARNING - click below to stay on"
        $Status.Text="Shutting down in "+([int][math]::Ceiling([math]::Max(0,$remaining)))+" seconds"
        $script:warnTicks++
        if(($script:warnTicks % [math]::Max(1,[int](2000/$TickMs))) -eq 0){ Force-Front }
        if($remaining -le 0){ Invoke-Shutdown }
    }
})

$ExtendBtn.Add_Click({ Invoke-Extend })

# New lengths apply right away to a running check period; during a final
# warning they apply from the next check.
$SaveBtn.Add_Click({
    $m = 0; $w = 0
    if (-not [int]::TryParse($CheckMinBox.Text.Trim(), [ref]$m) -or $m -lt $MinCheckMinutes -or $m -gt $MaxCheckMinutes) {
        $SettingsMsg.Text = "Check interval must be $MinCheckMinutes-$MaxCheckMinutes minutes."; return
    }
    if (-not [int]::TryParse($WarnSecBox.Text.Trim(), [ref]$w) -or $w -lt $MinWarnSeconds -or $w -gt $MaxWarnSeconds) {
        $SettingsMsg.Text = "Final warning must be $MinWarnSeconds-$MaxWarnSeconds seconds."; return
    }
    $script:ExtendSeconds = $m * 60; $script:WarnSeconds = $w
    try { Save-Settings; $SettingsMsg.Text = "Saved. Used from now on and next time the app opens." }
    catch { $SettingsMsg.Text = "Applied, but could not save: $($_.Exception.Message)" }
    Show-Settings
    if ($script:phase -eq 'extend') { Invoke-Extend }
})
$CancelBtn.Add_Click({ $script:reallyClose=$true; $timer.Stop(); $win.Close() })
$win.Add_Closing({ if(-not $script:reallyClose -and -not $AllowCancel){ $_.Cancel=$true } })

# ----- go -----
$win.Add_ContentRendered({
    Show-Settings
    Invoke-Extend
    $Status.Text="Next check in "+(Fmt $ExtendSeconds)
    $timer.Start()
})
$win.ShowDialog() | Out-Null
