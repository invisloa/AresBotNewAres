# Release-StuckKeys.ps1
#
# Rozwiazuje problem "komputer sam wpisuje wwwwwwdd" po zamknieciu bota.
#
# Przyczyna: bot wysyla W/A/S/D przez user32!keybd_event (SendInput).
# Klawisz W jest trzymany w dolc (keydown) przez caly czas pracy kamery/ruchu,
# a KEYUP leci dopiero na koncu. Jesli proces bota zostanie zamkniety w trakcie
# (Stop debugging w VS, zamkniecie okna, kill, crash) - KEYUP nigdy nie leci,
# a Windows pamieta klawisz jako wcisniety az do restartu komputera.
#
# Ten skrypt wysyla sam KEYUP (bez keydown) dla wszystkich klawiszy wirtualnych,
# czyli "puszcza" zawieszone klawisze bez restartu systemu.
#
# Uzycie:
#   powershell -ExecutionPolicy Bypass -File Tools\Release-StuckKeys.ps1
#   powershell -ExecutionPolicy Bypass -File Tools\Release-StuckKeys.ps1 -StopBotDrivers

param(
    [switch]$StopBotDrivers
)

Add-Type -Namespace StuckKeys -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
'@

$KEYUP = 2

function Get-DownKeys {
    $down = @()
    foreach ($vk in 0x01..0xFE) {
        $s = [StuckKeys.Native]::GetAsyncKeyState($vk)
        if (($s -band 0x8000) -ne 0) { $down += ('0x{0:X2}' -f $vk) }
    }
    return $down
}

$before = Get-DownKeys
Write-Host ("Przed: wcisniete klawisze = [{0}]" -f ($before -join ', '))

# Najpierw klawisze bota (W/A/S/D + reszta skrotow), potem wszystko pozostale.
$botKeys = 0x57, 0x41, 0x53, 0x44, 0x51, 0x45, 0x52, 0x46, 0x54, 0x47, 0x20, 0x09, 0x0D, 0x1B, 0x31, 0x32, 0x33, 0x36, 0x37, 0x38
foreach ($vk in $botKeys) { [StuckKeys.Native]::keybd_event([byte]$vk, 0, $KEYUP, 0) }
foreach ($vk in 0x01..0xFE) { [StuckKeys.Native]::keybd_event([byte]$vk, 0, $KEYUP, 0) }
Start-Sleep -Milliseconds 300

$after = Get-DownKeys
Write-Host ("Po:    wcisniete klawisze = [{0}]" -f ($after -join ', '))
if ($after.Count -eq 0) { Write-Host "OK - wszystkie klawisze puszczone." }
else { Write-Host "UWAGA - nadal wcisniete: $(($after) -join ', ')" }

if ($StopBotDrivers) {
    Write-Host ""
    Write-Host "--- Sprzatanie sterownikow pozostawionych przez bota ---"
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "Pominieto: uruchom jako Administrator, aby zatrzymac/usunac sterowniki."
    }
    else {
        # Wlasny testowy driver pamieci (WDKTestCert jakub.olowiak) - zostaje w jadrze do restartu.
        & sc.exe stop SexyDriver 2>&1 | Out-String | Write-Host
        # Osierocona usluga sterownika o losowej nazwie (C:\Reforgix\ZGBHH juz nie istnieje).
        & sc.exe delete nEiqetG 2>&1 | Out-String | Write-Host
    }
}
