param(
    [string]$Agent = "free-qwen3-coder",
    [switch]$FullAccess = $false,
    [switch]$NoLaunch = $false
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class WinAPI {
    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static IntPtr FindByTitle(string titlePart) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            if (sb.ToString().ToLower().Contains(titlePart.ToLower())) {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != 0) { found = hWnd; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindByPid(uint targetPid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == targetPid) { found = hWnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Find-OpenCodeWindow {
    $hwnd = [WinAPI]::FindByTitle("opencode")
    if ($hwnd -ne [IntPtr]::Zero) { return $hwnd }

    $ocProc = Get-Process -Name "opencode" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ocProc) {
        $hwnd = [WinAPI]::FindByPid($ocProc.Id)
        if ($hwnd -ne [IntPtr]::Zero) { return $hwnd }
    }

    return [IntPtr]::Zero
}

function Activate-Window {
    param([IntPtr]$hWnd)
    if ($hWnd -eq [IntPtr]::Zero) { return $false }
    [WinAPI]::ShowWindowAsync($hWnd, 9) | Out-Null
    Start-Sleep -Milliseconds 100
    [WinAPI]::SetForegroundWindow($hWnd) | Out-Null
    Start-Sleep -Milliseconds 150
    return $true
}

function Send-ToOpenCode {
    param([string]$Text)
    $consoleHwnd = Find-OpenCodeWindow
    if ($consoleHwnd -eq [IntPtr]::Zero) { return $false }

    [System.Windows.Forms.Clipboard]::SetText($Text)
    if (Activate-Window $consoleHwnd) {
        Start-Sleep -Milliseconds 200
        [System.Windows.Forms.SendKeys]::SendWait("+{INSERT}")
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        return $true
    }
    return $false
}

$bg = [System.Drawing.Color]::FromArgb(30, 30, 30)
$bg2 = [System.Drawing.Color]::FromArgb(45, 45, 45)
$fg = [System.Drawing.Color]::White
$fgDim = [System.Drawing.Color]::FromArgb(180, 180, 180)
$accent = [System.Drawing.Color]::FromArgb(0, 120, 212)

$font = New-Object System.Drawing.Font("Segoe UI", 9)
$fontBold = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)

$form = New-Object System.Windows.Forms.Form
$form.Text = "OpenCode Paste Helper"
$form.Size = New-Object System.Drawing.Size(640, 460)
$form.MinimumSize = New-Object System.Drawing.Size(480, 360)
$form.StartPosition = "CenterScreen"
$form.Font = $font
$form.BackColor = $bg
$form.ForeColor = $fg

try { $form.Icon = [System.Drawing.Icon]::ExtractAssociatedIcon((Get-Command opencode -ErrorAction Stop).Source) } catch {}

$header = New-Object System.Windows.Forms.Panel
$header.Size = New-Object System.Drawing.Size(640, 40)
$header.Dock = "Top"
$header.BackColor = $bg2

$headerText = New-Object System.Windows.Forms.Label
$headerText.Text = "Paste Helper - Ctrl+V works here"
$headerText.Location = New-Object System.Drawing.Point(12, 8)
$headerText.Size = New-Object System.Drawing.Size(400, 24)
$headerText.Font = $fontBold
$header.ForeColor = $fg
$header.Controls.Add($headerText)

$hint = New-Object System.Windows.Forms.Label
$hint.Text = "Paste text (Ctrl+V), click Send - it goes to opencode"
$hint.Location = New-Object System.Drawing.Point(12, 48)
$hint.Size = New-Object System.Drawing.Size(600, 20)
$hint.ForeColor = $fgDim

$inputBox = New-Object System.Windows.Forms.TextBox
$inputBox.Multiline = $true
$inputBox.ScrollBars = "Both"
$inputBox.Location = New-Object System.Drawing.Point(12, 74)
$inputBox.Size = New-Object System.Drawing.Size(600, 260)
$inputBox.Font = New-Object System.Drawing.Font("Consolas", 10)
$inputBox.AcceptsReturn = $true
$inputBox.WordWrap = $true
$inputBox.BackColor = [System.Drawing.Color]::FromArgb(22, 22, 22)
$inputBox.ForeColor = [System.Drawing.Color]::FromArgb(220, 220, 220)
$inputBox.BorderStyle = "FixedSingle"
$inputBox.Anchor = "Top, Left, Right, Bottom"

$btnSend = New-Object System.Windows.Forms.Button
$btnSend.Text = "Send to opencode"
$btnSend.Location = New-Object System.Drawing.Point(12, 345)
$btnSend.Size = New-Object System.Drawing.Size(160, 34)
$btnSend.FlatStyle = "Flat"
$btnSend.BackColor = $accent
$btnSend.ForeColor = [System.Drawing.Color]::White
$btnSend.Font = $fontBold
$btnSend.Cursor = "Hand"
$btnSend.Anchor = "Bottom, Left"
$btnSend.Add_Click({
    $text = $inputBox.Text.Trim()
    if (-not $text) {
        [System.Windows.Forms.MessageBox]::Show("Enter or paste text first", "Empty", "OK", [System.Windows.Forms.MessageBoxIcon]::Warning)
        return
    }
    $btnSend.Enabled = $false
    $btnSend.Text = "Sending..."
    $ok = Send-ToOpenCode $text
    if ($ok) {
        $inputBox.Clear()
        $status.Text = "Text sent to opencode"
        $form.WindowState = "Minimized"
    } else {
        [System.Windows.Forms.Clipboard]::SetText($text)
        $status.Text = "opencode window not found. Text copied (use Shift+Insert in terminal)"
    }
    $btnSend.Enabled = $true
    $btnSend.Text = "Send to opencode"
    $inputBox.Focus()
})

$btnPaste = New-Object System.Windows.Forms.Button
$btnPaste.Text = "Paste from clipboard"
$btnPaste.Location = New-Object System.Drawing.Point(182, 345)
$btnPaste.Size = New-Object System.Drawing.Size(140, 34)
$btnPaste.FlatStyle = "Flat"
$btnPaste.BackColor = [System.Drawing.Color]::FromArgb(60, 60, 60)
$btnPaste.ForeColor = $fg
$btnPaste.Cursor = "Hand"
$btnPaste.Anchor = "Bottom, Left"
$btnPaste.Add_Click({
    if ([System.Windows.Forms.Clipboard]::ContainsText()) {
        $inputBox.Paste()
    } else {
        $status.Text = "Clipboard is empty"
    }
})

$btnClear = New-Object System.Windows.Forms.Button
$btnClear.Text = "Clear"
$btnClear.Location = New-Object System.Drawing.Point(332, 345)
$btnClear.Size = New-Object System.Drawing.Size(90, 34)
$btnClear.FlatStyle = "Flat"
$btnClear.BackColor = [System.Drawing.Color]::FromArgb(60, 60, 60)
$btnClear.ForeColor = $fg
$btnClear.Cursor = "Hand"
$btnClear.Anchor = "Bottom, Left"
$btnClear.Add_Click({ $inputBox.Clear(); $inputBox.Focus() })

$btnHide = New-Object System.Windows.Forms.Button
$btnHide.Text = "Minimize to tray"
$btnHide.Location = New-Object System.Drawing.Point(432, 345)
$btnHide.Size = New-Object System.Drawing.Size(120, 34)
$btnHide.FlatStyle = "Flat"
$btnHide.BackColor = [System.Drawing.Color]::FromArgb(60, 60, 60)
$btnHide.ForeColor = $fg
$btnHide.Cursor = "Hand"
$btnHide.Anchor = "Bottom, Left"
$btnHide.Add_Click({ $form.WindowState = "Minimized" })

$status = New-Object System.Windows.Forms.Label
$status.Text = "Ready. Paste text (Ctrl+V) and click Send."
$status.Location = New-Object System.Drawing.Point(12, 388)
$status.Size = New-Object System.Drawing.Size(600, 22)
$status.ForeColor = $fgDim
$status.Anchor = "Bottom, Left, Right"

$form.Controls.AddRange(@($header, $hint, $inputBox, $btnSend, $btnPaste, $btnClear, $btnHide, $status))

$notifyIcon = New-Object System.Windows.Forms.NotifyIcon
try { $notifyIcon.Icon = $form.Icon } catch {}
$notifyIcon.Text = "OpenCode Paste Helper"
$notifyIcon.Visible = $false
$notifyIcon.Add_Click({
    $form.WindowState = "Normal"
    $form.ShowInTaskbar = $true
    $form.Activate()
    $inputBox.Focus()
})

$form.Add_Resize({
    if ($form.WindowState -eq "Minimized") {
        $form.ShowInTaskbar = $false
        $notifyIcon.Visible = $true
    } else {
        $form.ShowInTaskbar = $true
        $notifyIcon.Visible = $false
    }
})

if (-not $NoLaunch) {
    $ocProc = Get-Process -Name "opencode" -ErrorAction SilentlyContinue
    if (-not $ocProc) {
        $status.Text = "Starting opencode..."
        $ocArgs = @()
        if ($FullAccess) {
            $ocArgs += "--dangerously-skip-permissions"
        } else {
            $ocArgs += "--agent", $Agent, "--dangerously-skip-permissions"
        }
        try {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = "opencode"
            $psi.Arguments = $ocArgs
            $psi.UseShellExecute = $true
            $psi.WorkingDirectory = (Get-Item $PSScriptRoot).Parent.FullName
            $proc = [System.Diagnostics.Process]::Start($psi)
            Start-Sleep -Seconds 3
            $status.Text = "opencode started. Paste text and click Send."
        } catch {
            $status.Text = "Failed to start: $($_.Exception.Message)"
        }
    }
}

$form.Add_Shown({ $inputBox.Focus() })
[System.Windows.Forms.Application]::Run($form)
