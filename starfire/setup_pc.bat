@echo off
setlocal
echo ============================================
echo  STARFIRE COGITATOR PRIME -- SETUP
echo ============================================

python --version >NUL 2>&1
if errorlevel 1 (
    echo ERROR: python not found on PATH
    exit /b 1
)

python -c "import sys; v=sys.version_info; sys.exit(0 if (3,10)<=v<(3,13) else 1)"
if errorlevel 1 (
    echo ERROR: unsupported Python version.
    echo        Mediapipe and onnxruntime-directml have no Python 3.13 wheels yet.
    echo        Install Python 3.10, 3.11, or 3.12 from https://www.python.org/downloads/
    echo        and re-run this script.
    python --version
    exit /b 1
)

echo [1/4] Installing Python packages...
echo   - removing any pre-existing onnxruntime variants (they conflict with onnxruntime-directml)
python -m pip uninstall -y onnxruntime onnxruntime-gpu onnxruntime-azure onnxruntime-silicon >NUL 2>&1
python -m pip install --upgrade pip
python -m pip install onnxruntime-directml opencv-python websockets mediapipe numpy zeroconf psutil ultralytics

echo [2/4] Exporting YOLOv8l to ONNX...
if exist yolov8l.onnx (
    echo   yolov8l.onnx already present -- skipping
) else (
    python -c "from ultralytics import YOLO; YOLO('yolov8l.pt').export(format='onnx', opset=12, dynamic=False, simplify=True)"
    if exist yolov8l.onnx (
        echo   yolov8l.onnx exported
    ) else (
        echo   WARNING: export failed -- you can drop a yolov8l.onnx into this folder manually
    )
)

echo [3/4] Downloading MiDaS v2.1 small ONNX...
if exist midas_v21_small.onnx (
    echo   midas_v21_small.onnx already present -- skipping
) else (
    powershell -NoProfile -Command "try { Invoke-WebRequest -Uri 'https://github.com/isl-org/MiDaS/releases/download/v2_1/model-small.onnx' -OutFile 'midas_v21_small.onnx' -UseBasicParsing } catch { Write-Host 'download failed: ' $_ }"
    if exist midas_v21_small.onnx (
        echo   midas_v21_small.onnx downloaded
    ) else (
        echo   WARNING: download failed -- fetch manually from isl-org/MiDaS releases
    )
)

echo [4/4] Probing ONNX Runtime providers...
python -c "import onnxruntime as ort; print('Providers:', ort.get_available_providers())"

echo.
echo COGITATOR PRIME -- SETUP COMPLETE
endlocal
