@echo off
setlocal

set "SOURCE=\\192.168.0.230\wwwroot\DevelopTi\DevelopTi"
set "DEST=%LOCALAPPDATA%\DevTi"
set "MARKER=DevelopTi.dll"

rem Se o DevelopTi ja estiver aberto nesta maquina, nao mexe (evita travar em arquivo em uso e nao abre uma segunda janela).
tasklist /FI "IMAGENAME eq DevelopTi.exe" 2>nul | find /I "DevelopTi.exe" >nul
if not errorlevel 1 (
    exit /b 0
)

rem So sincroniza se a versao no servidor mudou desde a ultima vez (compara so 1 arquivo
rem marcador - tamanho e data). O app publicado tem centenas de arquivos (DLLs de idioma,
rem cache do WebView2 etc) e o robocopy /MIR precisa varrer tudo isso pela rede so pra
rem descobrir que nao ha nada novo pra copiar - era isso que continuava lento toda vez,
rem mesmo com a copia local ja feita.
set "SRC_INFO="
set "DST_INFO="
for %%F in ("%SOURCE%\%MARKER%") do set "SRC_INFO=%%~zF-%%~tF"
for %%F in ("%DEST%\%MARKER%") do set "DST_INFO=%%~zF-%%~tF"

if not "%SRC_INFO%"=="%DST_INFO%" (
    rem Copia so o que mudou (robocopy /MIR compara data/tamanho) para uma pasta local -
    rem rodar direto pela rede e que deixava lento, nao o app em si.
    rem Exclui a pasta de perfil/cache do WebView2: ela muda a cada uso do app e nao deve
    rem ficar na pasta publicada/compartilhada - cada maquina cria a sua propria localmente.
    robocopy "%SOURCE%" "%DEST%" /MIR /MT:16 /R:1 /W:1 /XD "DevelopTi.exe.WebView2" /NFL /NDL /NJH /NJS /NP >nul
)

start "" "%DEST%\DevelopTi.exe"
endlocal
