@echo off
setlocal
title SQLFlow - iniciando...

set "SOURCE=\\192.168.0.230\wwwroot\SQLFlow"
set "DEST=%LOCALAPPDATA%\SQLFlow"
set "MARKER=SQLFlow.dll"

rem Se o SQLFlow ja estiver aberto nesta maquina, nao mexe (evita travar em arquivo em uso e nao abre uma segunda janela).
tasklist /FI "IMAGENAME eq SQLFlow.exe" 2>nul | find /I "SQLFlow.exe" >nul
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

rem O marcador sozinho nao basta: se o SQLFlow.exe sumiu da pasta local (ex.: removido por
rem antivirus/EDR depois de uma copia anterior) mas o SQLFlow.dll continua batendo com o do
rem servidor, o bloco abaixo seria pulado pra sempre e o app nunca mais abriria sozinho -
rem forca a sincronizacao tambem quando o executavel nao existe, mesmo com marcador igual.
if not exist "%DEST%\SQLFlow.exe" set "SRC_INFO=forcar-sync"

if not "%SRC_INFO%"=="%DST_INFO%" (
    rem Copia so o que mudou (robocopy /MIR compara data/tamanho) para uma pasta local -
    rem rodar direto pela rede e que deixava lento, nao o app em si.
    rem Exclui a pasta de perfil/cache do WebView2: ela muda a cada uso do app e nao deve
    rem ficar na pasta publicada/compartilhada - cada maquina cria a sua propria localmente.
    echo Atualizando SQLFlow, aguarde...
    title SQLFlow - atualizando, pode levar alguns minutos na primeira vez...
    robocopy "%SOURCE%" "%DEST%" /MIR /MT:16 /R:1 /W:1 /XD "SQLFlow.exe.WebView2" /NFL /NDL /NJH /NJS /NP >nul

    rem O robocopy preserva o stream Zone.Identifier (Mark of the Web) dos arquivos
    rem copiados do compartilhamento de rede. Isso faz o Windows tratar o SQLFlow.exe
    rem local como "de origem nao confiavel" - em algumas maquinas isso aciona
    rem politicas de DLP/EDR que bloqueiam captura de tela (print) so nessa janela.
    rem Remove a marcacao apos copiar, pra rodar como um app local normal.
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%DEST%' -Recurse -File | Unblock-File" >nul 2>&1
    echo. > "%DEST%\.unblocked"
) else if not exist "%DEST%\.unblocked" (
    rem Instalacao local ja existia de antes desse ajuste (nenhum arquivo mudou no
    rem servidor, entao o robocopy acima nem rodou) - desbloqueia uma vez so aqui
    rem tambem, pra nao depender de uma atualizacao futura pra corrigir.
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%DEST%' -Recurse -File | Unblock-File" >nul 2>&1
    echo. > "%DEST%\.unblocked"
)

title SQLFlow - abrindo...
start "" "%DEST%\SQLFlow.exe"
endlocal
