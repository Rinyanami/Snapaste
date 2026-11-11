# Offline OCR models

Snapaste uses the PP-OCRv6 small multilingual detector and recognizer, the matching small character dictionary, and the PP-LCNet x0.25 text-line orientation classifier from PP-OCRv5. The models are the RapidOCR team's ONNX conversions of PaddleOCR models and are distributed under Apache 2.0. The matching dictionary is from the same release.

- [RapidOCR v3.10.0 model registry](https://github.com/RapidAI/RapidOCR/blob/v3.10.0/python/rapidocr/default_models.yaml)
- [RapidOCR model distribution](https://www.modelscope.cn/models/RapidAI/RapidOCR)
- [PaddleOCR source and model project](https://github.com/PaddlePaddle/PaddleOCR)
- [RapidOcrNet 4.2.0 source](https://github.com/BobLd/RapidOcrNet/tree/708cae2fcb88720e1d891a81b5ee3e8b2bcc139e)

Run `scripts/Get-OcrModels.ps1` before building from a clean checkout. The script checks each file against its pinned SHA-256, downloads only missing or changed files, and stages each replacement until it has verified the contents. The build copies the files to `ocr-models` alongside the executable. Snapaste only loads local files from that directory.

The same script verifies and extracts the four x64 Visual C++ 14.51.36247 runtime DLLs needed by ONNX Runtime from the official Visual Studio redistributable package. The build places these DLLs beside Snapaste for application-local use. See [Microsoft Visual C++ Runtime license terms](https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/) and [Visual Studio 2026 redistribution list](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution).
