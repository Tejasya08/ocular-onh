@echo off
REM ---------------------------------------------------------------------------
REM Ocular -- double-click launcher.
REM
REM Opens the Inbox folder and starts the watcher. Drop a fundus photograph
REM into the window that opens; the simulator builds and launches by itself.
REM
REM Plain ASCII only. A .bat saved with non-ASCII punctuation can fail to parse
REM on a machine with a different codepage, and the error it gives is useless.
REM ---------------------------------------------------------------------------

title Ocular - Fundus Image Pipeline
cd /d "%~dp0"

echo.
echo  ===========================================================
echo   OCULAR - drop a fundus image, get it in the simulator
echo  ===========================================================
echo.

if "%TRIPO_API_KEY%"=="" goto nokey

echo  Tripo API key: found.
echo.
echo  Opening the Inbox folder...
start "" "%~dp0Inbox"

echo.
echo  Drop your eye image into that folder.
echo    - the FILENAME becomes the patient name
echo    - put OD or OS in the name to set which eye
echo      e.g.  Kartikey_OD.jpg  ->  "Kartikey OD", right eye
echo.
echo  Each image takes a few minutes: Tripo generation, then the
echo  fit, then a rebuild. The simulator launches on its own.
echo.
echo  Close this window to stop watching.
echo.

python Tools\ocular_watch.py
goto end

:nokey
echo  TRIPO_API_KEY is not set, so 3D generation cannot run.
echo.
echo  Set it once, then re-run this file:
echo.
echo      setx TRIPO_API_KEY "tsk_your_key_here"
echo.
echo  Get the key from https://developers.tripo3d.ai/en/keys
echo  (a Tripo subscription is NOT the same thing as an API key).
echo.
echo  After setx you MUST open a new window - setx does not affect
echo  the one it ran in.
echo.
echo  ---------------------------------------------------------
echo  Starting WITHOUT Tripo: your image will still be measured
echo  and shown as a fitted nerve head, just with no 3D eye
echo  around it. Nothing clinical depends on that model.
echo  ---------------------------------------------------------
echo.
start "" "%~dp0Inbox"
python Tools\ocular_watch.py --no-tripo

:end
echo.
pause
