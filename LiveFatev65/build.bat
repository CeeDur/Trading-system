@echo off
REM Builds the LNN v64k binaries with the .NET Framework compiler that ships with Windows.
REM Run this from the folder that contains the .cs files. Output exes land in the same folder.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo csc.exe not found - .NET Framework 4.x is required & exit /b 1 )
echo Using %CSC%

"%CSC%" /nologo /optimize+ /target:winexe /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:LnnLiveDashboard.exe LnnLiveDashboard.cs || goto :fail
"%CSC%" /nologo /optimize+ /target:exe /r:System.dll /r:System.Core.dll /out:LnnLiveLearner_HybridTemporalMmf_v12.exe LnnLiveLearner_HybridTemporalMmf_v12.cs || goto :fail
"%CSC%" /nologo /optimize+ /target:exe /r:System.dll /r:System.Core.dll /out:LnnNestedHog.exe LnnNestedHog.cs || goto :fail
"%CSC%" /nologo /optimize+ /target:exe /r:System.dll /r:System.Core.dll /out:LnnExecBacktest.exe LnnExecBacktest.cs || goto :fail
"%CSC%" /nologo /optimize+ /target:exe /r:System.dll /r:System.Core.dll /out:RingReplayer.exe RingReplayer.cs || goto :fail

echo.
echo Built: LnnLiveDashboard.exe  LnnLiveLearner_HybridTemporalMmf_v12.exe  LnnNestedHog.exe  LnnExecBacktest.exe  RingReplayer.exe
echo Copy the engine exe to the path shown in the dashboard's engine dropdown (keep its filename), and LnnOrderExecStrategy.cs into NT8 (recompile there).
exit /b 0
:fail
echo BUILD FAILED - see the error above.
exit /b 1
